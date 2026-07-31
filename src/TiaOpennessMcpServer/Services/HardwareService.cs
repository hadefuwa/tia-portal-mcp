using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

public sealed class HardwareService
{
    private readonly TiaPortalService  _tia;
    private readonly StaTaskScheduler  _sta;
    private readonly TiaOpennessOptions _opts;
    private readonly ILogger<HardwareService> _log;

    public HardwareService(
        TiaPortalService tia,
        StaTaskScheduler sta,
        IOptions<TiaOpennessOptions> opts,
        ILogger<HardwareService> log)
    {
        _tia  = tia;
        _sta  = sta;
        _opts = opts.Value;
        _log  = log;
    }

    // ── Device enumeration ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync()
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var results = new List<DeviceInfo>();
            foreach (Device device in _tia.Project!.Devices)
                results.Add(ReadDevice(device));
            return (IReadOnlyList<DeviceInfo>)results;
        });
    }

    public async Task<DeviceInfo> GetDeviceAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var device = FindDevice(deviceName);
            return ReadDevice(device);
        });
    }

    // ── S7-1200 generation ────────────────────────────────────────────────────

    public async Task<DeviceInfo> GenerateS71200Async(S71200Config config)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            _log.LogInformation("Generating S7-1200 station: {Name} ({Cpu})",
                config.DeviceName, config.CpuVariant);

            if (!S71200OrderNumbers.Cpus.TryGetValue(config.CpuVariant, out var cpuOrderNum))
                throw new ArgumentException(
                    $"Unknown CPU variant '{config.CpuVariant}'. " +
                    $"Valid values: {string.Join(", ", S71200OrderNumbers.Cpus.Keys)}");

            Device device = _tia.Project!.Devices.CreateWithItem(
                cpuOrderNum, config.DeviceName, config.DeviceName);

            if (config.EnableProfinet)
                ConfigureProfinet(device, config);

            foreach (var sbKey in config.SignalBoards)
                AddModuleToDevice(device, sbKey, S71200OrderNumbers.SignalBoards, "SB");

            foreach (var smKey in config.SignalModules)
                AddModuleToDevice(device, smKey, S71200OrderNumbers.SignalModules, "SM");

            foreach (var cmKey in config.CommsModules)
                AddModuleToDevice(device, cmKey, S71200OrderNumbers.CommsModules, "CM");

            _log.LogInformation("S7-1200 station '{Name}' created successfully.", config.DeviceName);
            return ReadDevice(device);
        });
    }

    // ── I/O mapping ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<IoPoint>> GetIoMappingAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var device = FindDevice(deviceName);
            var points = new List<IoPoint>();
            foreach (DeviceItem item in device.DeviceItems)
                CollectIoPoints(item, points);
            return (IReadOnlyList<IoPoint>)points;
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ConfigureProfinet(Device device, S71200Config config)
    {
        foreach (DeviceItem di in device.DeviceItems)
        {
            var netInterface = di.GetService<NetworkInterface>();
            if (netInterface is null) continue;

            foreach (Node node in netInterface.Nodes)
            {
                try { node.SetAttribute("Address", config.IpAddress); } catch { }

                // Create subnet and connect node in one step.
                try
                {
                    var subnet = node.CreateAndConnectToSubnet("PN/IE_1");
                    if (subnet is not null && !string.IsNullOrWhiteSpace(config.SubnetMask))
                        try { subnet.SetAttribute("SubnetMask", config.SubnetMask); } catch { }
                }
                catch { /* subnet may already exist */ }

                if (!string.IsNullOrWhiteSpace(config.Gateway))
                    try { node.SetAttribute("DefaultGateway", config.Gateway); } catch { }
            }
        }
    }

    private static void AddModuleToDevice(
        Device device, string moduleKey,
        Dictionary<string, string> catalog,
        string categoryLabel)
    {
        if (!catalog.TryGetValue(moduleKey, out var orderNum))
            throw new ArgumentException(
                $"Unknown {categoryLabel} '{moduleKey}'. " +
                $"Valid values: {string.Join(", ", catalog.Keys)}");

        foreach (DeviceItem di in device.DeviceItems)
        {
            if (di.CanPlugNew(orderNum, "", -1))
            {
                di.PlugNew(orderNum, "", -1);
                return;
            }
        }

        throw new InvalidOperationException(
            $"No available slot found for {categoryLabel} '{moduleKey}'.");
    }

    private Device FindDevice(string name)
    {
        return _tia.Project!.Devices
            .Cast<Device>()
            .FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Device '{name}' not found in project.");
    }

    private static DeviceInfo ReadDevice(Device device)
    {
        var modules = new List<ModuleInfo>();
        foreach (DeviceItem item in device.DeviceItems)
            ReadModuleInfo(item, modules);

        var cpuItem = device.DeviceItems.Cast<DeviceItem>()
            .FirstOrDefault(di => di.Classification == DeviceItemClassifications.CPU);

        string typeId = "";
        try { typeId = device.TypeIdentifier ?? ""; } catch { }

        return new DeviceInfo
        {
            Name           = device.Name,
            TypeIdentifier = typeId,
            DeviceType     = typeId.Contains(':') ? typeId.Split(':').Last() : typeId,
            CpuModel       = cpuItem?.Name ?? "N/A",
            IpAddress      = ReadIpAddress(device),
            SubnetMask     = ReadSubnetMask(device),
            Gateway        = "",
            SlotCount      = modules.Count,
            Modules        = modules,
        };
    }

    private static void ReadModuleInfo(DeviceItem item, List<ModuleInfo> list)
    {
        if (item.Classification != DeviceItemClassifications.None)
        {
            string orderNum = "";
            try { orderNum = item.GetAttribute("OrderNumber") as string ?? ""; } catch { }

            list.Add(new ModuleInfo
            {
                Slot          = item.PositionNumber,
                Name          = item.Name,
                OrderNumber   = orderNum,
                ModuleType    = item.Classification.ToString(),
                InputAddress  = ReadAddressSpace(item, AddressIoType.Input),
                OutputAddress = ReadAddressSpace(item, AddressIoType.Output),
            });
        }

        foreach (DeviceItem child in item.DeviceItems)
            ReadModuleInfo(child, list);
    }

    private static string ReadIpAddress(Device device)
    {
        try
        {
            foreach (DeviceItem di in device.DeviceItems)
            {
                var ni = di.GetService<NetworkInterface>();
                if (ni is null) continue;
                foreach (Node n in ni.Nodes)
                {
                    var addr = n.GetAttribute("Address") as string;
                    if (!string.IsNullOrWhiteSpace(addr)) return addr;
                }
            }
        }
        catch { }
        return "";
    }

    private static string ReadSubnetMask(Device device)
    {
        try
        {
            foreach (DeviceItem di in device.DeviceItems)
            {
                var ni = di.GetService<NetworkInterface>();
                if (ni is null) continue;
                foreach (Node n in ni.Nodes)
                {
                    var mask = n.GetAttribute("SubnetMask") as string;
                    if (!string.IsNullOrWhiteSpace(mask)) return mask;
                }
            }
        }
        catch { }
        return "";
    }

    private static string ReadAddressSpace(DeviceItem item, AddressIoType ioType)
    {
        try
        {
            var addr = item.Addresses.Cast<Address>()
                .FirstOrDefault(a => a.IoType == ioType);
            if (addr is null) return "";
            bool isIn = ioType == AddressIoType.Input;
            return $"%{(isIn ? "I" : "Q")}{addr.StartAddress}..{addr.StartAddress + addr.Length - 1}";
        }
        catch { return ""; }
    }

    private static void CollectIoPoints(DeviceItem item, List<IoPoint> points)
    {
        try
        {
            foreach (Address addr in item.Addresses.Cast<Address>())
            {
                if (addr.IoType == AddressIoType.Input)
                {
                    for (int i = addr.StartAddress; i < addr.StartAddress + addr.Length; i++)
                        points.Add(new IoPoint
                        {
                            Address    = $"%I{i}",
                            SymbolName = "",
                            DataType   = "Byte",
                            Comment    = $"Input byte from {item.Name}",
                            IsInput    = true,
                        });
                }
                else if (addr.IoType == AddressIoType.Output)
                {
                    for (int i = addr.StartAddress; i < addr.StartAddress + addr.Length; i++)
                        points.Add(new IoPoint
                        {
                            Address    = $"%Q{i}",
                            SymbolName = "",
                            DataType   = "Byte",
                            Comment    = $"Output byte from {item.Name}",
                            IsInput    = false,
                        });
                }
            }
        }
        catch { }

        foreach (DeviceItem child in item.DeviceItems)
            CollectIoPoints(child, points);
    }
}
