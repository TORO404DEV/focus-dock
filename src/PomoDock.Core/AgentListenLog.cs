namespace PomoDock.Core;

public enum AgentListenLogKind { Music, Noise, Transcript, Wake, Command, Armed, Timeout, Error, Miss }

public sealed record AgentListenLogEntry(DateTime At, AgentListenLogKind Kind, string Detail)
{
    public static AgentListenLogEntry Now(AgentListenLogKind kind, string detail = "") =>
        new(DateTime.Now, kind, detail ?? "");
}
