using System.Diagnostics;

namespace TrafficRadar;

/// <summary>
/// Synthetic intersection for testing without the game: own truck drives north, brakes to a
/// stop line, waits, then pulls away. Cross traffic on an east-west road, oncoming traffic southbound.
/// Coordinates follow SCS conventions: x = east, z = south, yaw counter-clockwise from north.
/// </summary>
public sealed class DemoSource : IFrameSource
{
    private const double North = 0, West = Math.PI / 2, South = Math.PI, East = -Math.PI / 2;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private sealed record Lane(double Px, double Pz, double Dx, double Dz, double Yaw);

    private static readonly Lane Westbound = new(0, -2, -1, 0, West);
    private static readonly Lane Eastbound = new(0, 2, 1, 0, East);
    private static readonly Lane Southbound = new(-2, 0, 0, 1, South);

    // lane, speed m/s, phase offset m, width, length, trailer length (0 = none)
    private static readonly (Lane Lane, double V, double Offset, float W, float L, float Trailer)[] Traffic =
    [
        (Westbound, 13, 0, 1.9f, 4.6f, 0),
        (Westbound, 13, 140, 2.0f, 5.2f, 0),
        (Westbound, 11, 260, 2.5f, 6.2f, 13.6f),
        (Eastbound, 12, 60, 1.8f, 4.3f, 0),
        (Eastbound, 12, 200, 2.5f, 6.0f, 13.6f),
        (Eastbound, 14, 330, 1.9f, 4.8f, 0),
        (Southbound, 10, 90, 1.9f, 4.5f, 0),
        (Southbound, 10, 300, 2.5f, 9.0f, 0),
    ];

    public string Status => "Demo";

    public RawFrame? Read()
    {
        double t = _clock.Elapsed.TotalSeconds;
        double ct = t % 40;
        double ownZ;
        if (ct < 10) { double v = 1.1 * (10 - ct); ownZ = 12 + v * v / 2.2; } // brake 40 km/h -> 0
        else if (ct < 18) ownZ = 12;                                      // wait at stop line
        else { double dt = ct - 18; ownZ = 12 - 0.5 * dt * dt; }           // pull away

        var vehicles = new List<RawVehicle>();
        int id = 1;
        foreach (var (lane, v, offset, w, l, trailer) in Traffic)
        {
            double s = ((t * v + offset) % 400) - 200;
            double x = lane.Px + lane.Dx * s, z = lane.Pz + lane.Dz * s;
            var rot = Quat.FromYaw(lane.Yaw);
            var trailers = new List<RawBox>();
            if (trailer > 0)
            {
                double back = l / 2 + 0.6 + trailer / 2;
                trailers.Add(new RawBox(x - lane.Dx * back, z - lane.Dz * back, rot, 2.55f, trailer));
            }
            vehicles.Add(new RawVehicle(id++, x, z, rot, w, l, (float)v, false, false, trailers));
        }

        // parked van at the corner
        vehicles.Add(new RawVehicle(id, 9, 9, Quat.FromYaw(North), 2.0f, 5.5f, 0, false, false, []));

        int lightState = ct < 16 ? 2 : ct < 18 ? 4 : 8;
        var lights = new List<RawSemaphore>
        {
            new(1, 5, 9, Quat.FromYaw(North), 1, lightState, 0),
            new(2, -5, -9, Quat.FromYaw(South), 1, lightState, 0),
            new(3, 9, -5, Quat.FromYaw(West), 1, lightState == 8 ? 2 : 8, 0),
            new(4, -9, 5, Quat.FromYaw(East), 1, lightState == 8 ? 2 : 8, 0),
        };

        return new RawFrame(2, ownZ, Quat.FromYaw(North), vehicles, lights);
    }
}
