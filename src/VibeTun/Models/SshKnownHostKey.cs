namespace VibeTun.Models;

/// <summary>SSH host-key fingerprint accepted by the user for a host, port and key algorithm.</summary>
public sealed class SshKnownHostKey
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Algorithm { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}
