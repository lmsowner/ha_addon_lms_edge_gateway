// Reference DTOs for the new .NET projects. No server implementation is claimed.
namespace LMS.Energy.Core;

public enum MeasurementQuality { Good, Unmapped, Missing, Unavailable, Stale, InvalidUnit, Derived }
public sealed record EnergyMeasurement(double? Value, string Unit, MeasurementQuality Quality,
    string? EntityId, DateTimeOffset ObservedAt, DateTimeOffset? MeasuredAt);
public sealed record EntityBinding(string EntityId, string? Pointer = null, string? SourceUnit = null,
    bool Invert = false, int MaxAgeSeconds = 0, string? RegistryId = null, string? DeviceId = null);
public sealed record MappingConfiguration(long Revision, System.Text.Json.JsonElement Layout,
    Dictionary<string, string[]> Cells, Dictionary<string, EntityBinding> Mappings);
public sealed record MappingWrite(long ExpectedRevision, System.Text.Json.JsonElement Layout,
    Dictionary<string, string[]> Cells, Dictionary<string, EntityBinding> Mappings);
public interface IHomeAssistantEntityCatalogue
{
    Task<IReadOnlyList<System.Text.Json.JsonElement>> GetEntitiesAsync(CancellationToken cancellationToken);
}
public interface IEnergyMappingStore
{
    Task<MappingConfiguration> LoadAsync(CancellationToken cancellationToken);
    Task<MappingConfiguration> SaveAsync(MappingWrite write, CancellationToken cancellationToken);
}
