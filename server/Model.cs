namespace TrafficRadar;

/// <summary>Quaternion rotation as delivered by the game (w, x, y, z).</summary>
public readonly record struct Quat(float W, float X, float Y, float Z)
{
    public static Quat FromYaw(double yawRad) =>
        new((float)Math.Cos(yawRad / 2), 0, (float)Math.Sin(yawRad / 2), 0);

    /// <summary>
    /// Horizontal forward vector (x, z). SCS models face -Z; world x points east, z points south.
    /// </summary>
    public (double Fx, double Fz) Forward()
    {
        double fx = -2.0 * (X * Z + W * Y);
        double fz = -(1.0 - 2.0 * (X * X + Y * Y));
        double len = Math.Sqrt(fx * fx + fz * fz);
        return len < 1e-6 ? (0, -1) : (fx / len, fz / len);
    }
}

public sealed record RawBox(double X, double Z, Quat Rot, float Width, float Length);

public sealed record RawVehicle(
    int Id, double X, double Z, Quat Rot, float Width, float Length,
    float Speed, bool IsTmp, bool IsTrailer, List<RawBox> Trailers);

/// <summary>Type 1 = traffic light, 2 = gate.</summary>
public sealed record RawSemaphore(int Id, double X, double Z, Quat Rot, int Type, int State, float TimeRemaining);

public sealed record RawFrame(
    double TruckX, double TruckZ, Quat TruckRot,
    List<RawVehicle> Vehicles, List<RawSemaphore> Semaphores,
    double? ExactSpeedMs = null); // from radar_launcher.dll (official telemetry), if loaded

public interface IFrameSource
{
    /// <summary>Human-readable state, shown when <see cref="Read"/> returns null.</summary>
    string Status { get; }
    RawFrame? Read();
}

// ---- Snapshot sent to the browser (all positions relative to own truck, in metres) ----

public sealed record BoxDto(double Dx, double Dz, double Fx, double Fz, double W, double L);

public sealed record VehicleDto(int Id, double Dx, double Dz, double Fx, double Fz, double W, double L,
    double V, List<BoxDto> Trailers);

public sealed record LightDto(double Dx, double Dz, double Fx, double Fz, int Type, int State, double Rem);

/// <summary>Hdg = geographic heading in degrees; Conv = meridian convergence in radians (see Ets2Projection).</summary>
public sealed record Snapshot(bool Ok, string Status, double SpeedKmh, double Fx, double Fz,
    List<VehicleDto> Vehicles, List<LightDto> Lights, double Hdg = 0, double Conv = 0);
