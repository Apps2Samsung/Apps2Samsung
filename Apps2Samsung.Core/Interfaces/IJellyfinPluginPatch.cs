using Apps2Samsung.Helpers.Jellyfin.Plugins;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface IJellyfinPluginPatch
    {
        //string PluginName { get; }
        Task ApplyAsync(PluginPatchContext ctx);
    }

}
