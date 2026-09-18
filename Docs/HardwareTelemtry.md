# Hardware & Device Monitoring

This part of the BaronDesk Agent is responsible for collecting local hardware telemetry and monitoring device changes.

The monitoring components collect information locally and publish it through one central telemetry pipeline.

---

## 1. Architecture

The current flow is:

```text
Hardware sensors
      ↓
HardwareSensorReader
      ↓
HardwareMonitorService
      ↓
TelemetryService
      ↓
ITelemetryTransport
      ↓
LoggingTelemetryTransport
```

Device monitoring follows the same central pipeline:

```text
Windows device events
      ↓
WindowsDeviceMonitorService
      ↓
DeviceTelemetry
      ↓
TelemetryService
      ↓
ITelemetryTransport
      ↓
LoggingTelemetryTransport
```

The important point is that both hardware telemetry and device events are handled by the same `TelemetryService`.

The monitoring services do not communicate directly with the backend.

---

# 2. Hardware Monitoring

Hardware information is collected using **LibreHardwareMonitorLib**.

The main files involved are:

```text
BaronDeskAgent.ServiceCore
└── Hardware
    ├── HardwareSensorReader.cs
    └── HardwareMonitorService.cs
```

### `HardwareSensorReader.cs`

This class communicates with LibreHardwareMonitor and reads the available hardware sensors.

It is responsible for:

- Discovering hardware
- Updating sensor values
- Reading CPU information
- Reading RAM information
- Reading GPU information
- Reading fan information
- Converting the sensor values into the shared telemetry model

It does not handle scheduling or network communication.

Its job is:

```text
LibreHardwareMonitor
        ↓
HardwareSensorReader
        ↓
HardwareTelemetry
```

---

# 3. CPU Telemetry

CPU information is represented by:

```csharp
CpuTelemetry
```

The model contains:

```text
Name
Vendor
TemperatureC
LoadPercent
CoreMaxLoadPercent
```

CPU detection is based on the hardware type rather than assuming a particular processor vendor.

The reader selects the most appropriate available CPU sensors and converts their values into `CpuTelemetry`.

This allows the same model and monitoring flow to work with different CPU configurations.

---

# 4. RAM Telemetry

RAM information is represented by:

```csharp
RamTelemetry
```

The available values are:

```text
UsedGb
AvailableGb
TotalGb
UsagePercent
```

The reader obtains the available memory sensor values and calculates total memory or usage percentage when necessary.

---

# 5. GPU Telemetry

GPU information is represented by:

```csharp
GpuTelemetry
```

and stored in:

```csharp
List<GpuTelemetry>
```

Each GPU can contain:

```text
Name
Vendor
TemperatureC
HotSpotTemperatureC
MemoryTemperatureC
LoadPercent
MemoryUsedMb
MemoryFreeMb
MemoryTotalMb
```

Using a list allows the same telemetry model to represent multiple GPUs.

The hardware reader identifies the available GPU hardware internally and converts it into the common `GpuTelemetry` structure.

---

# 6. Fan Telemetry

Fan information is represented by:

```csharp
FanTelemetry
```

with:

```text
Name
SpeedRpm
```

Fans are stored in a list because a machine can contain multiple fan sensors.

---

# 7. Shared Hardware Telemetry Model

The shared model is located at:

```text
BaronDesk.Shared
└── Models
    └── HardwareTelemetry.cs
```

The structure is:

```text
HardwareTelemetry
│
├── CpuTelemetry
├── RamTelemetry
├── GpuTelemetry[]
└── FanTelemetry[]
```

This model is shared because it represents the telemetry data that moves between components and will eventually be sent outside the ServiceCore.

---

# 8. Hardware Monitoring Service

File:

```text
BaronDeskAgent.ServiceCore/Hardware/HardwareMonitorService.cs
```

This service is responsible for periodically collecting hardware telemetry.

The current interval is:

```text
5 seconds
```

Its flow is:

```text
Every 5 seconds
      ↓
HardwareSensorReader.ReadTelemetry()
      ↓
HardwareTelemetry
      ↓
TelemetryService.PublishHardwareAsync()
```

`PeriodicTimer` is used to schedule the readings.

---

# 9. Device Monitoring

Device monitoring is implemented by:

```text
BaronDeskAgent.ServiceCore/Hardware/WindowsDeviceMonitorService.cs
```

This service listens for Windows device-instance notifications using the Windows Configuration Manager API.

The notification flow is:

```text
Windows
   ↓
Device notification
   ↓
Native callback
   ↓
Internal Channel
   ↓
WindowsDeviceMonitorService
   ↓
DeviceChangeEvent
   ↓
DeviceTelemetry
   ↓
TelemetryService
```

The native callback only handles the notification and places the event into a channel.

The actual processing happens asynchronously inside the service.

---

# 10. Device Enumeration

File:

```text
BaronDeskAgent.ServiceCore/Hardware/WindowsDeviceEnumerator.cs
```

This class is responsible for obtaining information about devices from Windows.

It handles:

- Enumerating currently present devices
- Looking up a device by instance ID
- Reading the Windows device name
- Extracting the Product ID
- Creating a canonical device key

The Windows device name is obtained from the device properties, using the friendly name when available and falling back to the device description.

---

# 11. Device Change Event

File:

```text
BaronDeskAgent.ServiceCore/Hardware/Models/DeviceChangeEvent.cs
```

`DeviceChangeEvent` is an internal ServiceCore model.

It represents the event as it is processed locally:

```text
DeviceName
ProductId
DeviceType
EventType
Timestamp
```

It is then converted into the shared telemetry model.

---

# 12. Device Telemetry

File:

```text
BaronDesk.Shared/Models/DeviceTelemetry.cs
```

This is the shared representation of a device event:

```text
Timestamp
DeviceType
DeviceName
ProductId
EventType
```

The flow is:

```text
Windows event
      ↓
DeviceChangeEvent
      ↓
DeviceTelemetry
```

`DeviceTelemetry` is what gets published to the central telemetry service.

---

# 13. Central Telemetry Service

File:

```text
BaronDeskAgent.ServiceCore/Services/TelemetryService.cs
```

`TelemetryService` is the central point for telemetry.

It receives data from different monitoring components and puts it into a shared processing queue.

The service uses a thread-safe `Channel<T>`.

Hardware telemetry and device events therefore use the same pipeline:

```text
HardwareMonitorService
        │
        │
        ▼
TelemetryService
        ▲
        │
WindowsDeviceMonitorService
```

The telemetry service is responsible for:

- Creating the telemetry envelope
- Assigning a unique ID
- Assigning the timestamp
- Assigning the sequence number
- Sending the envelope to the configured transport

---

# 14. Telemetry Envelope

File:

```text
BaronDesk.Shared/Models/TelemetryEnvelope.cs
```

All telemetry passes through the same envelope structure:

```json
{
  "type": "string",
  "id": "uuid",
  "ts": "iso-8601",
  "seq": 123,
  "payload": {}
}
```

For example, hardware telemetry uses:

```text
type = telemetry
```

and device events use:

```text
type = device_event
```

The payload contains the corresponding shared telemetry object.

---

# 15. Telemetry Transport

The transport abstraction is:

```text
BaronDeskAgent.ServiceCore/Services/ITelemetryTransport.cs
```

The interface allows `TelemetryService` to remain independent of the destination of the telemetry.

The current implementation is:

```text
LoggingTelemetryTransport.cs
```

The current flow is:

```text
TelemetryService
      ↓
ITelemetryTransport
      ↓
LoggingTelemetryTransport
      ↓
Console
```

The transport converts the telemetry into readable output.

For example:

```text
Hardware | CPU: AMD AMD Ryzen 5 7535HS with Radeon Graphics | Load: 1.10% | Temp: N/A | Core Max: 8.46% | RAM: 20.75/27.69 GB (74.95%)

GPU | AMD | AMD Radeon(TM) Graphics | Load: 0.00% | Temp: 46.0°C | VRAM: 1132/4096 MB

GPU | NVIDIA | NVIDIA GeForce RTX 4050 Laptop GPU | Load: 0.00% | Temp: 41.0°C | VRAM: 220/6141 MB
```

A device event is displayed through the same transport:

```text
Device Connected | USB | Périphérique USB composite | PID: 07C0
```

---

# 16. File Structure

The current monitoring-related files are:

```text
BaronDeskAgent.ServiceCore
│
├── Hardware
│   ├── HardwareMonitorService.cs
│   ├── HardwareSensorReader.cs
│   ├── WindowsDeviceMonitorService.cs
│   ├── WindowsDeviceEnumerator.cs
│   │
│   └── Models
│       └── DeviceChangeEvent.cs
│
└── Services
    ├── ITelemetryTransport.cs
    ├── LoggingTelemetryTransport.cs
    └── TelemetryService.cs
```

Shared telemetry models:

```text
BaronDesk.Shared
└── Models
    ├── HardwareTelemetry.cs
    ├── DeviceTelemetry.cs
    └── TelemetryEnvelope.cs
```

---

# 17. Complete Data Flow

The complete current flow can be summarized as:

```text
                         HARDWARE
                            │
                            ▼
                 HardwareSensorReader
                            │
                            ▼
                 HardwareMonitorService
                            │
                            │
                            ▼
                    TelemetryService
                            ▲
                            │
                            │
              WindowsDeviceMonitorService
                            ▲
                            │
                            │
                    Windows device API
                            │
                         DEVICES
```

Both sources publish through:

```text
TelemetryService
      ↓
Telemetry Envelope
      ↓
ITelemetryTransport
      ↓
LoggingTelemetryTransport
      ↓
Readable telemetry output
```

This gives the ServiceCore one central telemetry path while keeping hardware collection, device monitoring, telemetry processing, and transport responsibilities separated.
