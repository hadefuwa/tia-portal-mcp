namespace TiaOpennessMcpServer.Models;

// ── S7-1200 CPU order numbers (V4.5 firmware) ─────────────────────────────────
public static class S71200OrderNumbers
{
    // CPU variants: AC/DC/Relay, DC/DC/DC, DC/DC/Relay
    public static readonly Dictionary<string, string> Cpus = new()
    {
        ["1211C-DC/DC/DC"]    = "OrderNumber:6ES7 211-1AE40-0XB0/V4.5",
        ["1211C-DC/DC/Relay"] = "OrderNumber:6ES7 211-1BE40-0XB0/V4.5",
        ["1211C-AC/DC/Relay"] = "OrderNumber:6ES7 211-1CE40-0XB0/V4.5",
        ["1212C-DC/DC/DC"]    = "OrderNumber:6ES7 212-1AE40-0XB0/V4.5",
        ["1212C-DC/DC/Relay"] = "OrderNumber:6ES7 212-1BE40-0XB0/V4.5",
        ["1212C-AC/DC/Relay"] = "OrderNumber:6ES7 212-1CE40-0XB0/V4.5",
        ["1214C-DC/DC/DC"]    = "OrderNumber:6ES7 214-1AG40-0XB0/V4.5",
        ["1214C-DC/DC/Relay"] = "OrderNumber:6ES7 214-1BG40-0XB0/V4.5",
        ["1214C-AC/DC/Relay"] = "OrderNumber:6ES7 214-1CG40-0XB0/V4.5",
        ["1215C-DC/DC/DC"]    = "OrderNumber:6ES7 215-1AG40-0XB0/V4.5",
        ["1215C-DC/DC/Relay"] = "OrderNumber:6ES7 215-1BG40-0XB0/V4.5",
        ["1215C-AC/DC/Relay"] = "OrderNumber:6ES7 215-1CG40-0XB0/V4.5",
        ["1217C-DC/DC/DC"]    = "OrderNumber:6ES7 217-1AG40-0XB0/V4.5",
    };

    // Signal modules (expand left of CPU)
    public static readonly Dictionary<string, string> SignalModules = new()
    {
        // Digital Input
        ["SM1221-8DI-24VDC"]  = "OrderNumber:6ES7 221-1BF32-0XB0/V2.0",
        ["SM1221-16DI-24VDC"] = "OrderNumber:6ES7 221-1BH32-0XB0/V2.0",
        // Digital Output
        ["SM1222-8DO-24VDC"]  = "OrderNumber:6ES7 222-1BF32-0XB0/V2.0",
        ["SM1222-8DO-Relay"]  = "OrderNumber:6ES7 222-1HF32-0XB0/V2.0",
        ["SM1222-16DO-24VDC"] = "OrderNumber:6ES7 222-1BH32-0XB0/V2.0",
        // Digital I/O
        ["SM1223-8DI-8DO-24VDC"]   = "OrderNumber:6ES7 223-1BH32-0XB0/V2.0",
        ["SM1223-16DI-16DO-24VDC"] = "OrderNumber:6ES7 223-1PL32-0XB0/V2.0",
        // Analog Input
        ["SM1231-4AI-13bit"]  = "OrderNumber:6ES7 231-4HD32-0XB0/V3.0",
        ["SM1231-8AI-13bit"]  = "OrderNumber:6ES7 231-4HF32-0XB0/V3.0",
        ["SM1231-4AI-RTD"]    = "OrderNumber:6ES7 231-5ND32-0XB0/V2.0",
        ["SM1231-4AI-TC"]     = "OrderNumber:6ES7 231-5QD32-0XB0/V2.0",
        // Analog Output
        ["SM1232-2AO-14bit"]  = "OrderNumber:6ES7 232-4HB32-0XB0/V2.0",
        ["SM1232-4AO-14bit"]  = "OrderNumber:6ES7 232-4HD32-0XB0/V2.0",
        // Analog I/O
        ["SM1234-4AI-2AO"]    = "OrderNumber:6ES7 234-4HE32-0XB0/V2.0",
    };

    // Signal boards (plug into CPU)
    public static readonly Dictionary<string, string> SignalBoards = new()
    {
        ["SB1221-4DI-5VDC"]   = "OrderNumber:6ES7 221-3BD30-0XB0/V1.0",
        ["SB1222-4DO-5VDC"]   = "OrderNumber:6ES7 222-1BD30-0XB0/V1.0",
        ["SB1223-2DI-2DO"]    = "OrderNumber:6ES7 223-0BD30-0XB0/V1.0",
        ["SB1231-1AI"]        = "OrderNumber:6ES7 231-4HA30-0XB0/V1.0",
        ["SB1232-1AO"]        = "OrderNumber:6ES7 232-4HA30-0XB0/V1.0",
        ["CB1241-RS485"]      = "OrderNumber:6ES7 241-1CH30-1XB0/V1.0",
    };

    // Communications modules (expand right of CPU)
    public static readonly Dictionary<string, string> CommsModules = new()
    {
        ["CM1241-RS232"]       = "OrderNumber:6ES7 241-1AH32-0XB0/V2.1",
        ["CM1241-RS485"]       = "OrderNumber:6ES7 241-1CH32-0XB0/V2.1",
        ["CM1241-RS422-485"]   = "OrderNumber:6ES7 241-1FH32-0XB0/V2.1",
        ["CM1243-5-ProfibusDP"]= "OrderNumber:6GK7 243-5DX30-0XE0/V1.0",
        ["CM1242-5-ProfibusSlave"]= "OrderNumber:6GK7 242-5DX30-0XE0/V1.0",
        ["CP1242-7-GPRS"]      = "OrderNumber:6GK7 242-7KX30-0XE0/V1.0",
    };
}

// ── Configuration DTOs ────────────────────────────────────────────────────────
public sealed record S71200Config
{
    public required string DeviceName   { get; init; }
    public required string CpuVariant   { get; init; }   // Key in S71200OrderNumbers.Cpus
    public required string IpAddress    { get; init; }
    public required string SubnetMask   { get; init; }
    public required string Gateway      { get; init; }
    public          IReadOnlyList<string> SignalModules { get; init; } = [];
    public          IReadOnlyList<string> SignalBoards  { get; init; } = [];
    public          IReadOnlyList<string> CommsModules  { get; init; } = [];
    public          bool   EnableProfinet { get; init; } = true;
}
