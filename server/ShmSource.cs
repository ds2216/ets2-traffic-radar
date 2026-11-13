using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace TrafficRadar;

/// <summary>
/// Reads the shared memory published by the ETS2LA game plugin
/// (https://github.com/ETS2LA/plugin, layouts from src/core.hpp).
/// </summary>
public sealed class ShmSource : IFrameSource, IDisposable
{
    private const string CameraName = @"Local\ETS2LACameraProps";
    private const string TrafficName = @"Local\ETS2LATraffic";
    private const string SemaphoreName = @"Local\ETS2LASemaphore";
    private const string SpeedName = @"Local\TrafficRadarSpeed"; // written by radar_launcher.dll
    private const int SpeedSize = 13;
    private const uint SpeedMagic = 0x50535254; // "TRSP"

    private const int CameraSize = 128;
    private const int TrafficSize = 6960;   // 40 x (54 vehicle + 3 x 40 trailer)
    private const int VehicleRecord = 174;
    private const int SemaphoreSize = 1920; // 40 x 48
    private const int SemaphoreRecord = 48;

    private static readonly string[] GameProcesses = ["eurotrucks2", "amtrucks"];

    private readonly byte[] _cam = new byte[CameraSize];
    private readonly byte[] _traffic = new byte[TrafficSize];
    private readonly byte[] _sem = new byte[SemaphoreSize];

    private readonly byte[] _speed = new byte[SpeedSize];
    private Mapping? _camMap, _trafficMap, _semMap, _speedMap;
    private long _nextOpenAttempt, _nextSpeedOpenAttempt;
    private long _nextProcessCheck;
    private bool _gameRunning;
    private int _gamePid;
    private readonly byte[] _lastCam = new byte[CameraSize];
    private long _lastHealthyAt, _lastReopenAt;
    private int _reopens;

    public string Status { get; private set; } = "Starte …";

    public RawFrame? Read()
    {
        long now = Environment.TickCount64;

        if (now >= _nextProcessCheck)
        {
            _nextProcessCheck = now + 1000;
            int pid = GameProcesses.SelectMany(Process.GetProcessesByName).Select(p => p.Id).FirstOrDefault();
            _gameRunning = pid != 0;
            // Game gone or restarted: drop our handles, otherwise they keep the old (dead) mapping alive.
            if (pid != _gamePid)
                CloseAll();
            _gamePid = pid;
        }

        if (!_gameRunning)
        {
            Status = "ETS2 läuft nicht.";
            return null;
        }

        // radar_launcher.dll reports whether the simulation runs (SDK events started/paused).
        // While in the menu, loading or paused, the ETS2LA data is meaningless – and mappings opened
        // during loading can stay empty – so only open them once the drive actually runs.
        if (_speedMap is null && now >= _nextSpeedOpenAttempt)
        {
            _nextSpeedOpenAttempt = now + 2000;
            _speedMap = Mapping.TryOpen(SpeedName, SpeedSize);
        }
        if (_speedMap is not null)
        {
            _speedMap.Read(_speed);
            if (BinaryPrimitives.ReadUInt32LittleEndian(_speed) == SpeedMagic && _speed[12] != 0)
            {
                CloseEts2la();
                _lastHealthyAt = now;
                SetStatus("Spiel pausiert oder im Menü – das Radar startet mit der Fahrt.", default);
                return null;
            }
        }

        if (_camMap is null || _trafficMap is null)
        {
            if (now < _nextOpenAttempt)
                return null;
            _nextOpenAttempt = now + 2000;
            _camMap ??= Mapping.TryOpen(CameraName, CameraSize);
            _trafficMap ??= Mapping.TryOpen(TrafficName, TrafficSize);
            _semMap ??= Mapping.TryOpen(SemaphoreName, SemaphoreSize);
            if (_camMap is null || _trafficMap is null)
            {
                Status = "ETS2 läuft, aber das ETS2LA-Plugin liefert keine Daten (nicht installiert oder Spielversion passt nicht).";
                return null;
            }
        }

        _camMap.Read(_cam);
        _trafficMap.Read(_traffic);
        _semMap?.Read(_sem);

        ReadOnlySpan<byte> cam = _cam;
        double tx = F(cam, 100), tz = F(cam, 108);
        var trot = new Quat(F(cam, 112), F(cam, 116), F(cam, 120), F(cam, 124));
        bool valid = !(tx == 0 && tz == 0 && trot.W == 0);
        bool changed = !cam.SequenceEqual(_lastCam);
        if (changed)
            _cam.CopyTo(_lastCam, 0);
        if (valid && changed)
            _lastHealthyAt = now;

        // Opened while the game was still loading, the data can stay empty or frozen for good.
        // Reopening is cheap, so do it every 3 s until the plugin delivers live data.
        if (now - _lastHealthyAt > 3000 && now - _lastReopenAt > 3000)
        {
            _lastReopenAt = now;
            _reopens++;
            CloseEts2la();
            _nextOpenAttempt = 0;
            return Read(); // _lastReopenAt prevents another round
        }

        if (!valid)
        {
            SetStatus("Plugin geladen – warte auf Fahrt (Spiel im Menü?).", cam);
            return null;
        }

        SetStatus("OK", cam);
        return new RawFrame(tx, tz, trot, ParseVehicles(), ParseSemaphores(), ReadExactSpeed());
    }

    private void SetStatus(string status, ReadOnlySpan<byte> cam)
    {
        if (status == Status)
            return;
        Status = status;
        // goes to server.log when started by radar_launcher.dll
        string data = cam.Length < CameraSize ? "" :
            $" | fov={F(cam, 0):0.#} truck=({F(cam, 100):0.#}, {F(cam, 108):0.#}) rotW={F(cam, 112):0.##}";
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {status}{data} reopens={_reopens}");
    }

    private double? ReadExactSpeed()
    {
        if (_speedMap is null)
            return null;
        _speedMap.Read(_speed);
        if (BinaryPrimitives.ReadUInt32LittleEndian(_speed) != SpeedMagic)
            return null;
        return Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(_speed.AsSpan(8)));
    }

    private List<RawVehicle> ParseVehicles()
    {
        var list = new List<RawVehicle>(40);
        ReadOnlySpan<byte> all = _traffic;
        for (int i = 0; i < 40; i++)
        {
            var r = all.Slice(i * VehicleRecord, VehicleRecord);
            short id = BinaryPrimitives.ReadInt16LittleEndian(r[50..]);
            float width = F(r, 28), length = F(r, 36);
            if (id <= 0 || length <= 0) // unused slots are zero-filled
                continue;

            short trailerCount = BinaryPrimitives.ReadInt16LittleEndian(r[48..]);
            bool isTmp = r[52] != 0, isTrailer = r[53] != 0;
            var trailers = new List<RawBox>();
            for (int k = 0; k < 3; k++)
            {
                var t = r.Slice(54 + k * 40, 40);
                float tl = F(t, 36);
                // AI vehicles report trailer_count; TMP trailer chains leave it at 0 but fill the slots.
                if (tl <= 0 || (!isTmp && k >= trailerCount))
                    continue;
                trailers.Add(new RawBox(F(t, 0), F(t, 8), new Quat(F(t, 12), F(t, 16), F(t, 20), F(t, 24)), F(t, 28), tl));
            }

            list.Add(new RawVehicle(id, F(r, 0), F(r, 8), new Quat(F(r, 12), F(r, 16), F(r, 20), F(r, 24)),
                width, length, F(r, 40), isTmp, isTrailer, trailers));
        }
        return list;
    }

    private List<RawSemaphore> ParseSemaphores()
    {
        var list = new List<RawSemaphore>();
        if (_semMap is null)
            return list;
        ReadOnlySpan<byte> all = _sem;
        for (int i = 0; i < 40; i++)
        {
            var r = all.Slice(i * SemaphoreRecord, SemaphoreRecord);
            int type = BinaryPrimitives.ReadInt32LittleEndian(r[32..]);
            if (type == 0)
                continue;
            short cx = BinaryPrimitives.ReadInt16LittleEndian(r[12..]);
            short cz = BinaryPrimitives.ReadInt16LittleEndian(r[14..]);
            // semaphore positions are sector-local: global = sector * 512 + local
            list.Add(new RawSemaphore(
                BinaryPrimitives.ReadInt32LittleEndian(r[44..]),
                cx * 512.0 + F(r, 0), cz * 512.0 + F(r, 8),
                new Quat(F(r, 16), F(r, 20), F(r, 24), F(r, 28)),
                type, BinaryPrimitives.ReadInt32LittleEndian(r[40..]), F(r, 36)));
        }
        return list;
    }

    private static float F(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadSingleLittleEndian(s[offset..]);

    private void CloseEts2la()
    {
        _camMap?.Dispose(); _camMap = null;
        _trafficMap?.Dispose(); _trafficMap = null;
        _semMap?.Dispose(); _semMap = null;
    }

    private void CloseAll()
    {
        CloseEts2la();
        _speedMap?.Dispose(); _speedMap = null;
    }

    public void Dispose() => CloseAll();

    private sealed class Mapping : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;

        private Mapping(MemoryMappedFile file, MemoryMappedViewAccessor view) { _file = file; _view = view; }

        public static Mapping? TryOpen(string name, int size)
        {
            try
            {
                var file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                var view = file.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
                return new Mapping(file, view);
            }
            catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        public void Read(byte[] target) => _view.ReadArray(0, target, 0, target.Length);

        public void Dispose() { _view.Dispose(); _file.Dispose(); }
    }
}
