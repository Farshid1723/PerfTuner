namespace PerfTuner.Contracts;

/// <summary>
/// Semantic validation (bounds, character sets) applied after the structural guard and
/// the strict deserializer. A request that fails here is never dispatched.
/// </summary>
public static class RequestValidator
{
    public static bool TryValidate(Request request, out string error)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Id == 0)
        {
            error = "id must be non-zero";
            return false;
        }

        switch (request)
        {
            case HelloRequest hello:
                if (!IsSafeName(hello.ClientName, ProtocolConstants.MaxClientNameLength))
                {
                    error = "clientName must be 1..64 chars of [A-Za-z0-9 ._-]";
                    return false;
                }
                break;

            case GetHistoryRequest history:
                if (history.MaxPoints is < 1 or > ProtocolConstants.MaxHistoryPoints)
                {
                    error = $"maxPoints must be 1..{ProtocolConstants.MaxHistoryPoints}";
                    return false;
                }
                break;

            case SetMonitoringOptionsRequest options:
                if (options.EnablePerProcessIo is null && options.AggregationIntervalMs is null)
                {
                    error = "at least one option must be set";
                    return false;
                }
                if (options.AggregationIntervalMs is { } ms &&
                    (ms < ProtocolConstants.MinAggregationIntervalMs || ms > ProtocolConstants.MaxAggregationIntervalMs))
                {
                    error = $"aggregationIntervalMs must be {ProtocolConstants.MinAggregationIntervalMs}..{ProtocolConstants.MaxAggregationIntervalMs}";
                    return false;
                }
                break;

            case GetProcessesRequest list:
                if (list.MaxCount is < 1 or > ProtocolConstants.MaxProcessListCount)
                {
                    error = $"maxCount must be 1..{ProtocolConstants.MaxProcessListCount}";
                    return false;
                }
                break;

            case ProcessActionRequest action:
                if (!TryValidateAction(action, out error))
                {
                    return false;
                }
                break;

            case RevertChangeRequest revert:
                if (revert.ChangeId <= 0)
                {
                    error = "changeId must be positive";
                    return false;
                }
                break;

            case SetTuningOptionsRequest options:
                if (options.SafeMode is null && options.Mode is null && options.AutoCalm is null && options.AutoCalmThresholdPercent is null)
                {
                    error = "at least one option must be set";
                    return false;
                }
                if (options.Mode is { } mode && !Enum.IsDefined(mode))
                {
                    error = "unknown mode";
                    return false;
                }
                if (options.AutoCalmThresholdPercent is { } t &&
                    (t < ProtocolConstants.MinAutoCalmThresholdPercent || t > ProtocolConstants.MaxAutoCalmThresholdPercent))
                {
                    error = $"autoCalmThresholdPercent must be {ProtocolConstants.MinAutoCalmThresholdPercent}..{ProtocolConstants.MaxAutoCalmThresholdPercent}";
                    return false;
                }
                break;

            case ReportForegroundRequest foreground:
                if (foreground.Pid <= 0)
                {
                    error = "pid must be positive";
                    return false;
                }
                break;

            case SetRulesRequest rules:
                if (!TryValidateRules(rules.Rules, out error))
                {
                    return false;
                }
                break;

            case PingRequest:
            case ShutdownEngineRequest:
            case GetStatusRequest:
            case SubscribeMetricsRequest:
            case GetConfigRequest:
            case GetChangesRequest:
            case RevertAllRequest:
            case ResetBreakerRequest:
            case GetRulesRequest:
            case TrimBackgroundMemoryRequest:
            case GetInsightsRequest:
            case GetBootReportRequest:
                break;

            case GetIoLatencyRequest:
            case GetNetworkHealthRequest:
            case GetWaitAnalysisRequest:
            case GetMemoryGovernorRequest:
            case GetContractsRequest:
            case GetRegressionsRequest:
            case GetExperimentsRequest:
                break;

            case GetTimelineRequest timeline:
                if (timeline.MaxEvents is < 1 or > ProtocolConstants.MaxTimelineEvents || timeline.SinceUnixMs < 0)
                {
                    error = $"maxEvents must be 1..{ProtocolConstants.MaxTimelineEvents}, sinceUnixMs ≥ 0";
                    return false;
                }
                break;

            case ExplainStallRequest explain:
                if (explain.EventId <= 0)
                {
                    error = "eventId must be positive";
                    return false;
                }
                break;

            case StartWaitAnalysisRequest wait:
                if (wait.Seconds < ProtocolConstants.MinWaitAnalysisSeconds || wait.Seconds > ProtocolConstants.MaxWaitAnalysisSeconds)
                {
                    error = $"seconds must be {ProtocolConstants.MinWaitAnalysisSeconds}..{ProtocolConstants.MaxWaitAnalysisSeconds}";
                    return false;
                }
                break;

            case SetProfileRequest profile:
                if (!Enum.IsDefined(profile.Profile))
                {
                    error = "unknown profile";
                    return false;
                }
                break;

            case StartExperimentRequest experiment:
                if (!IsSafeName(experiment.ContractId, 32) || experiment.Pid <= 4 || experiment.CreateTime <= 0)
                {
                    error = "invalid experiment request";
                    return false;
                }
                break;

            case StartLatencyScanRequest scan:
                if (scan.Seconds < ProtocolConstants.MinLatencyScanSeconds || scan.Seconds > ProtocolConstants.MaxLatencyScanSeconds)
                {
                    error = $"seconds must be {ProtocolConstants.MinLatencyScanSeconds}..{ProtocolConstants.MaxLatencyScanSeconds}";
                    return false;
                }
                break;

            default:
                // A new request type was added without a validation rule: fail closed.
                error = "unsupported request";
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateAction(ProcessActionRequest a, out string error)
    {
        error = string.Empty;
        if (a.Pid <= 4 || a.CreateTime <= 0)
        {
            error = "invalid process identity";
            return false;
        }
        if (!Enum.IsDefined(a.Action))
        {
            error = "unknown action";
            return false;
        }

        // Exactly the parameter each action needs — nothing more, nothing less.
        var ok = a.Action switch
        {
            ProcessAction.SetPriority => a.Priority is { } p && Enum.IsDefined(p) && Only(a, priority: true),
            ProcessAction.EfficiencyMode => a.Enable is not null && Only(a, enable: true),
            ProcessAction.CpuCores => a.Cores is { } c && Enum.IsDefined(c) && Only(a, cores: true),
            ProcessAction.MemoryPriority => a.MemoryPriority is { } m && Enum.IsDefined(m) && Only(a, memory: true),
            ProcessAction.SoftMemoryCap => a.CapMb is >= ProtocolConstants.MinSoftCapMb and <= ProtocolConstants.MaxSoftCapMb && Only(a, cap: true),
            ProcessAction.TrimMemory => Only(a),
            ProcessAction.EndProcess => a.Confirm == true && Only(a, confirm: true),
            _ => false,
        };
        if (!ok)
        {
            error = $"invalid or missing parameters for {a.Action}";
        }
        return ok;
    }

    private static bool Only(ProcessActionRequest a, bool priority = false, bool enable = false, bool cores = false, bool memory = false, bool cap = false, bool confirm = false)
        => (a.Priority is not null) == priority
        && (a.Enable is not null) == enable
        && (a.Cores is not null) == cores
        && (a.MemoryPriority is not null) == memory
        && (a.CapMb is not null) == cap
        && (a.Confirm is not null) == confirm;

    public static bool TryValidateRules(AppRule[]? rules, out string error)
    {
        error = string.Empty;
        if (rules is null || rules.Length > ProtocolConstants.MaxRules)
        {
            error = $"at most {ProtocolConstants.MaxRules} rules";
            return false;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule is null || !IsSafeName(rule.ImageName, ProtocolConstants.MaxProcessNameLength))
            {
                error = "rule imageName must be 1..64 chars of [A-Za-z0-9 ._-]";
                return false;
            }
            if (!names.Add(rule.ImageName))
            {
                error = "duplicate rule for " + rule.ImageName;
                return false;
            }
            if (rule.Priority is null && rule.EfficiencyMode is null && rule.Cores is null && rule.MemoryPriority is null)
            {
                error = "rule must set at least one action";
                return false;
            }
            if ((rule.Priority is { } p && !Enum.IsDefined(p)) || (rule.Cores is { } c && !Enum.IsDefined(c)) ||
                (rule.MemoryPriority is { } m && !Enum.IsDefined(m)))
            {
                error = "rule contains an unknown value";
                return false;
            }
        }
        return true;
    }

    /// <summary>Allow-list character check; used for anything that may reach a log line.</summary>
    public static bool IsSafeName(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }
        foreach (var c in value)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}
