namespace PerfTuner.Core.Security;

/// <summary>Outcome of one security check.</summary>
public enum SecurityStatus
{
    Good,

    /// <summary>Worth improving, not urgent.</summary>
    Warn,

    /// <summary>A real gap.</summary>
    Bad,

    /// <summary>Couldn't be checked (no rights, feature missing).</summary>
    Unknown,

    /// <summary>For your information only (not scored).</summary>
    Info,
}

/// <summary>Pure rules behind the Security page (unit-tested).</summary>
public static class SecurityRules
{
    /// <summary>
    /// "Unquoted service path": a service whose program path contains a space and isn't in quotes. Windows then tries
    /// "C:\Program.exe", "C:\Program Files\Vendor.exe"… first, so anyone able to write one of those files gets code run
    /// as the service (often SYSTEM). Only the part up to ".exe" matters; arguments may contain spaces.
    /// </summary>
    public static bool IsUnquotedServicePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) { return false; }
        var p = imagePath.TrimStart();
        if (p.StartsWith('"')) { return false; }
        var exe = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0) { return false; }
        return p[..exe].Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>The same command line with the program path in quotes (arguments unchanged).</summary>
    public static string QuoteServicePath(string imagePath)
    {
        if (!IsUnquotedServicePath(imagePath)) { throw new ArgumentException("not an unquoted service path", nameof(imagePath)); }
        var p = imagePath.TrimStart();
        var end = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
        return "\"" + p[..end] + "\"" + p[end..];
    }

    /// <summary>Antivirus signatures: up to a week old is fine, older than two weeks is a gap.</summary>
    public static SecurityStatus SignatureAge(uint days) => days <= 7 ? SecurityStatus.Good : days <= 14 ? SecurityStatus.Warn : SecurityStatus.Bad;

    /// <summary>Last successful Windows update: monthly patches mean > 35 days is late, > 60 days is a gap.</summary>
    public static SecurityStatus UpdateAge(TimeSpan age) => age.TotalDays <= 35 ? SecurityStatus.Good : age.TotalDays <= 60 ? SecurityStatus.Warn : SecurityStatus.Bad;

    /// <summary>Good checks out of all checks that could be judged (Unknown and Info don't count).</summary>
    public static (int Good, int Total) Score(IEnumerable<SecurityStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var judged = statuses.Where(s => s is SecurityStatus.Good or SecurityStatus.Warn or SecurityStatus.Bad).ToList();
        return (judged.Count(s => s == SecurityStatus.Good), judged.Count);
    }
}
