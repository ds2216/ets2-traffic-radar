namespace TrafficRadar;

/// <summary>
/// The ETS2 world is a flat Lambert conformal conic map of Europe, so the world's "up" axis (-z,
/// grid north) differs from geographic north away from the central meridian (15° E) – in Iceland by
/// about 30°. The in-game navigation shows geographic directions, so the compass corrects for it.
///
/// Parameters from the game's def/climate.sii, as used by TruckSim Maps
/// (github.com/truckermudgeon/maps, packages/libs/map/projections.ts).
/// </summary>
public static class Ets2Projection
{
    private const double Radius = 6_370_997;             // sphere, cancels out of the angle
    private const double FactorZ = -0.000171570875;      // mapFactor[0] (northing per game unit, sign-flipped)
    private const double FactorX = 0.0001729241463;      // mapFactor[1]
    private const double OffsetX = 16660, OffsetZ = 4150; // mapOffset
    private const double Lat1 = 37, Lat2 = 65, Lat0 = 50; // standard parallels, origin latitude (lon0 = 15)

    private static readonly double LengthOfDegree = Radius * Math.PI / 180;
    private static readonly double N;    // cone constant
    private static readonly double Rho0; // distance from the cone apex (north pole) to the origin

    static Ets2Projection()
    {
        static double T(double latDeg) => Math.Tan(Math.PI / 4 + latDeg * Math.PI / 360);
        double c1 = Math.Cos(Lat1 * Math.PI / 180), c2 = Math.Cos(Lat2 * Math.PI / 180);
        N = Math.Log(c1 / c2) / Math.Log(T(Lat2) / T(Lat1));
        double f = c1 * Math.Pow(T(Lat1), N) / N;
        Rho0 = Radius * f / Math.Pow(T(Lat0), N);
    }

    /// <summary>
    /// Meridian convergence at a world position, in radians: geographic heading = grid heading + result.
    /// Negative west of 15° E (Iceland ≈ -0.53), positive east of it.
    /// </summary>
    public static double Convergence(double x, double z)
    {
        // UK content is authored at a different scale (TruckSim Maps' "Calais" sector rule)
        int sx = (int)Math.Floor(x / 4000), sz = (int)Math.Floor(z / 4000);
        x -= OffsetX;
        z -= OffsetZ;
        if (sx <= -8 && sz <= -2 && !(sx == -8 && sz == -2))
        {
            x = (x - 15550) * 0.75;
            z = (z - 2750) * 0.75;
        }
        double easting = x * FactorX * LengthOfDegree;
        double northing = z * FactorZ * LengthOfDegree;
        return Math.Atan2(easting, Rho0 - northing);
    }

    /// <summary>Geographic heading in degrees (0 = north, clockwise) for a world forward vector.</summary>
    public static double Heading(double x, double z, double fx, double fz)
    {
        // grid heading in the projected plane (the two axes are scaled very slightly differently)
        double grid = Math.Atan2(fx * FactorX, fz * FactorZ);
        double deg = (grid + Convergence(x, z)) * 180 / Math.PI;
        return ((deg % 360) + 360) % 360;
    }
}
