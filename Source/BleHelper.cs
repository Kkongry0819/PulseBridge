using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

public static class BleHelper
{
    static readonly object gate = new object();
    static void Emit(object value)
    {
        lock (gate) { Console.WriteLine(new JavaScriptSerializer().Serialize(value)); Console.Out.Flush(); }
    }
    [MTAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length == 0 || args[0] == "scan") return Scan();
            if (args[0] == "connect" && args.Length == 2) return Connect(UInt64.Parse(args[1])).GetAwaiter().GetResult();
            throw new ArgumentException("Expected scan or connect <BluetoothAddress>");
        }
        catch (Exception ex) { Emit(new { type = "error", text = ex.Message }); return 1; }
    }
    static int Scan()
    {
        var seen = new HashSet<ulong>();
        var watcher = new BluetoothLEAdvertisementWatcher();
        watcher.ScanningMode = BluetoothLEScanningMode.Active;
        watcher.Received += delegate(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            var name = e.Advertisement.LocalName;
            if (String.IsNullOrWhiteSpace(name)) return;
            lock (seen) { if (!seen.Add(e.BluetoothAddress)) return; }
            bool hr = e.Advertisement.ServiceUuids.Contains(new Guid("0000180d-0000-1000-8000-00805f9b34fb"));
            Emit(new { type = "device", address = e.BluetoothAddress.ToString(), name = name, hr = hr, rssi = (int)e.RawSignalStrengthInDBm });
        };
        string failure = null;
        watcher.Stopped += delegate(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
        { if (e.Error != BluetoothError.Success) failure = e.Error.ToString(); };
        watcher.Start();
        try { for (int i = 0; i < 45 && failure == null; i++) Thread.Sleep(200); }
        finally { watcher.Stop(); }
        if (failure != null) throw new Exception("Bluetooth scan failed: " + failure);
        Emit(new { type = "status", text = "扫描完成。请开启心率广播；普通蓝牙配对不等于支持心率。" });
        return 0;
    }
    static async Task<T> Await<T>(Windows.Foundation.IAsyncOperation<T> operation)
    {
        var end = DateTime.UtcNow.AddSeconds(18);
        while (operation.Status == Windows.Foundation.AsyncStatus.Started) { if (DateTime.UtcNow > end) { operation.Cancel(); throw new TimeoutException("Bluetooth request timed out."); } await Task.Delay(50); }
        if (operation.Status != Windows.Foundation.AsyncStatus.Completed) throw new Exception("Bluetooth operation failed: " + operation.Status);
        return operation.GetResults();
    }
    static async Task<int> Connect(ulong address)
    {
        using (var device = await Await<BluetoothLEDevice>(BluetoothLEDevice.FromBluetoothAddressAsync(address)))
        {
            if (device == null) throw new Exception("Cannot open Bluetooth device.");
            var services = await Await<GattDeviceServicesResult>(device.GetGattServicesForUuidAsync(new Guid("0000180d-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached));
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) throw new Exception("Heart Rate Service (180D) unavailable. Enable heart rate broadcast.");
            using (var service = services.Services[0])
            {
                var chars = await Await<GattCharacteristicsResult>(service.GetCharacteristicsForUuidAsync(new Guid("00002a37-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached));
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) throw new Exception("Heart Rate Measurement (2A37) unavailable.");
                var characteristic = chars.Characteristics[0];
                characteristic.ValueChanged += delegate(GattCharacteristic sender, GattValueChangedEventArgs e)
                {
                    try
                    {
                        using (var reader = DataReader.FromBuffer(e.CharacteristicValue))
                        {
                            byte[] data = new byte[e.CharacteristicValue.Length]; reader.ReadBytes(data);
                            Emit(new { type = "sample", packet = Convert.ToBase64String(data) });
                        }
                    }
                    catch (Exception ex) { Emit(new { type = "error", text = ex.Message }); }
                };
                var status = await Await<GattCommunicationStatus>(characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify));
                if (status != GattCommunicationStatus.Success) throw new Exception("Cannot subscribe to heart rate notifications: " + status);
                Emit(new { type = "status", text = "蓝牙已连接，等待心率数据" });
                while (device.ConnectionStatus == BluetoothConnectionStatus.Connected) await Task.Delay(250);
                throw new Exception("Bluetooth disconnected.");
            }
        }
    }
}
