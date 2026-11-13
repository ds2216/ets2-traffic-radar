using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using TrafficRadar;

int port = 8350;
bool demo = false;
string? logFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--demo") demo = true;
    else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
    else if (args[i] == "--log" && i + 1 < args.Length) logFile = args[++i];
}

if (logFile is not null)
{
    // started hidden by radar_launcher.dll: send all console output to a file
    var writer = new StreamWriter(Path.Combine(AppContext.BaseDirectory, logFile), append: false) { AutoFlush = true };
    Console.SetOut(writer);
    Console.SetError(writer);
    Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} gestartet");
    AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.WriteLine("Absturz: " + e.ExceptionObject);
}

// the page is embedded into the exe, so the server is a single file
string page;
using (var res = typeof(RadarState).Assembly.GetManifestResourceStream("index.html")!)
using (var reader = new StreamReader(res))
    page = reader.ReadToEnd();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
var state = new RadarState();
IFrameSource source = demo ? new DemoSource() : new ShmSource();

_ = Task.Run(() => state.RunAsync(source, app.Lifetime.ApplicationStopping));

app.MapGet("/", () => Results.Text(page, "text/html; charset=utf-8"));
app.MapGet("/index.html", () => Results.Text(page, "text/html; charset=utf-8"));

app.MapGet("/state", () => Results.Text(state.Latest.Json, "application/json"));

// LAN address for phones/tablets, shown as QR code on the page (looked up per request: the IP can change)
app.MapGet("/info", () => Results.Json(new { lanUrl = LanUrl(port) }));
app.MapGet("/qr.svg", () =>
{
    var url = LanUrl(port);
    if (url is null) return Results.NotFound();
    using var data = new QRCoder.QRCodeGenerator().CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
    var svg = new QRCoder.SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true);
    return Results.Text(svg, "image/svg+xml");
});

app.MapGet("/events", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    // tell radar_launcher.dll that a page is open, so it doesn't open another browser tab
    if (EventWaitHandle.TryOpenExisting(@"Local\TrafficRadarClient", out var clientEvent))
        using (clientEvent) clientEvent.Set();
    long lastSeq = -1;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var (seq, json) = state.Latest;
            if (seq != lastSeq)
            {
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastSeq = seq;
            }
            await Task.Delay(40, ct);
        }
    }
    catch (OperationCanceledException) { }
});

Console.WriteLine($"ETS2 Verkehrsradar{(demo ? " (DEMO)" : "")} – Anzeige im Browser öffnen:");
Console.WriteLine($"  http://localhost:{port}/");
foreach (var ip in LanAddresses())
    Console.WriteLine($"  http://{ip}:{port}/");
Console.WriteLine($"QR-Code auf der Seite zeigt: {LanUrl(port) ?? "(keine Netzwerkadresse)"}");
Console.WriteLine("Beenden mit Strg+C.");

app.Run();

static IEnumerable<IPAddress> LanAddresses() => LanAddressesOf(NetworkInterface.GetAllNetworkInterfaces());

static IEnumerable<IPAddress> LanAddressesOf(IEnumerable<NetworkInterface> nics) =>
    nics.Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                    && !a.ToString().StartsWith("169.254."));

// The address a phone in the same network can reach: the adapter with a default gateway
// (skips VirtualBox/Hyper-V/WSL adapters, which have none).
static string? LanUrl(int port)
{
    var withGateway = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.GetIPProperties().GatewayAddresses
            .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)));
    var ip = LanAddressesOf(withGateway).FirstOrDefault() ?? LanAddresses().FirstOrDefault();
    return ip is null ? null : $"http://{ip}:{port}/";
}

namespace TrafficRadar
{
    public sealed class RadarState
    {
        private const double MaxRange = 300; // metres sent to the browser (largest view 150 m, corners further)
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly SpeedEstimator _speed = new();
        private volatile Holder _latest = new(0, Serialize(new Snapshot(false, "Starte …", 0, 0, -1, [], [])));

        private sealed record Holder(long Seq, string Json);

        public (long Seq, string Json) Latest { get { var h = _latest; return (h.Seq, h.Json); } }

        public async Task RunAsync(IFrameSource source, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            long seq = 0;
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Snapshot snap;
                try
                {
                    var frame = source.Read();
                    snap = frame is null
                        ? new Snapshot(false, source.Status, 0, 0, -1, [], [])
                        : Compose(frame, Speed(frame));
                    if (frame is null) _speed.Reset();
                }
                catch (Exception e)
                {
                    snap = new Snapshot(false, "Fehler: " + e.Message, 0, 0, -1, [], []);
                }
                _latest = new Holder(++seq, Serialize(snap));
            }
        }

        private double Speed(RawFrame frame)
        {
            double estimate = _speed.Update(frame.TruckX, frame.TruckZ); // keep the estimator warm
            return frame.ExactSpeedMs ?? estimate;
        }

        private static string Serialize(Snapshot s) => JsonSerializer.Serialize(s, Json);

        private static double R(double v) => Math.Round(v, 2);

        private static Snapshot Compose(RawFrame f, double speedMs)
        {
            var (tfx, tfz) = f.TruckRot.Forward();
            var vehicles = new List<VehicleDto>();
            foreach (var v in f.Vehicles)
            {
                double dx = v.X - f.TruckX, dz = v.Z - f.TruckZ;
                if (dx * dx + dz * dz > MaxRange * MaxRange) continue;
                var (fx, fz) = v.Rot.Forward();
                var trailers = v.Trailers.Select(t =>
                {
                    var (bx, bz) = t.Rot.Forward();
                    return new BoxDto(R(t.X - f.TruckX), R(t.Z - f.TruckZ), R(bx), R(bz), R(t.Width), R(t.Length));
                }).ToList();
                vehicles.Add(new VehicleDto(v.Id, R(dx), R(dz), R(fx), R(fz), R(v.Width), R(v.Length), R(v.Speed), trailers));
            }

            var lights = new List<LightDto>();
            foreach (var s in f.Semaphores)
            {
                double dx = s.X - f.TruckX, dz = s.Z - f.TruckZ;
                if (dx * dx + dz * dz > MaxRange * MaxRange) continue;
                var (fx, fz) = s.Rot.Forward();
                lights.Add(new LightDto(R(dx), R(dz), R(fx), R(fz), s.Type, s.State, R(s.TimeRemaining)));
            }

            return new Snapshot(true, "OK", R(speedMs * 3.6), R(tfx), R(tfz), vehicles, lights,
                Hdg: Math.Round(Ets2Projection.Heading(f.TruckX, f.TruckZ, tfx, tfz), 1),
                Conv: Math.Round(Ets2Projection.Convergence(f.TruckX, f.TruckZ), 4));
        }
    }

    /// <summary>
    /// Own-truck speed from position changes (the ETS2LA plugin exports no player speed).
    /// </summary>
    public sealed class SpeedEstimator
    {
        private double _x, _z, _v;
        private long _t;
        private bool _has;

        public void Reset() { _has = false; _v = 0; }

        public double Update(double x, double z)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_has) { (_x, _z, _t, _has) = (x, z, now, true); return _v; }
            double dt = (now - _t) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (dt < 0.04) return _v;
            double d = Math.Sqrt((x - _x) * (x - _x) + (z - _z) * (z - _z));
            (_x, _z, _t) = (x, z, now);
            if (d > 60) { _v = 0; return 0; } // teleport / ferry / reload
            _v += 0.35 * (d / dt - _v);
            return _v;
        }
    }
}
