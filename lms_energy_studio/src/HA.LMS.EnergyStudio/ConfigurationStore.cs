using System.Text.Json;
using System.Text.Json.Nodes;
using LMS.Energy.Core;
namespace HA.LMS.EnergyStudio;
public sealed class ConfigurationStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    readonly SemaphoreSlim gate=new(1,1);
    readonly string path;
    EnergyConfiguration current;
    public ConfigurationStore(IConfiguration settings)
    {
        path=Path.Combine(settings["EnergyStudio:DataRoot"]??"/data/lms-energy-studio","configuration.json");
        current=File.Exists(path)?JsonSerializer.Deserialize<EnergyConfiguration>(File.ReadAllText(path),Json)!:new(0,new JsonObject(),[],[]);
    }
    public EnergyConfiguration Get() => JsonSerializer.Deserialize<EnergyConfiguration>(JsonSerializer.Serialize(Volatile.Read(ref current),Json),Json)!;
    public async Task<EnergyConfiguration> Save(ConfigurationUpdate update,IReadOnlyDictionary<string,JsonObject> catalogue,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(update.ExpectedRevision!=current.Revision)throw new RevisionConflictException();
            Configuration.Validate(update,catalogue);
            var mappings=update.Mappings.ToDictionary(p=>p.Key,p=>p.Value with {RegistryId=catalogue[p.Value.EntityId]["registry_id"]?.ToString()});
            var next=new EnergyConfiguration(current.Revision+1,update.Layout,update.Cells,mappings);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary=path+".tmp";
            await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(next,Json),ct);
            File.Move(temporary,path,true);
            Volatile.Write(ref current,next);
            return Get();
        }
        finally{gate.Release();}
    }
}
public sealed class RevisionConflictException:Exception;
