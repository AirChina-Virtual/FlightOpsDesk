namespace VamSys.ProcessChild;

/// <summary>One line of the JSON protocol between the test process (acting as server) and the child.</summary>
public sealed record Wire(string Type, string? Point = null, string? Method = null, string? Path = null, string? Body = null, int Code = 200);
