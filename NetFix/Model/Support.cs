namespace Netfix.Model;

/// <summary>
/// Who a home user contacts when Netfix can't fix something. LagWatch runs on the user's own PC, so a network
/// "get help" step points to their internet provider. Change it here and every step follows.
/// </summary>
public static class Support
{
    public const string Name = "your internet provider";

    /// <summary>"Call your internet provider."</summary>
    public static string ContactStep => $"Call {Name}.";

    /// <summary>A last-resort network step: "Still not working? Call your internet provider."</summary>
    public static string StillNotWorking => "Still not working? " + ContactStep;

    /// <summary>A last-resort step for the PC itself (sound, camera, storage), where the internet provider can't help.</summary>
    public static string PcStillNotWorking => "Still not working? Contact your PC maker's support.";
}
