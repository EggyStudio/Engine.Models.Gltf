namespace Engine;

/// <summary>
/// Plugin that brings up the SharpGLTF backend for the model-import system. Registers
/// <see cref="GltfModelLoader"/> with the <see cref="AssetServer"/> and the matching
/// <see cref="GltfModelReader"/> with the <see cref="SceneReaderRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated backend:</b> Assimp can parse glTF too, but loses information on
/// PBR material networks, KHR_* extensions, animation interpolation modes, and morph
/// targets. SharpGLTF reads the schema directly so the resulting <see cref="SceneAsset"/>
/// is the highest-fidelity option for <c>.gltf</c> / <c>.glb</c>. This plugin is brought
/// up <i>after</i> <see cref="AssimpModelPlugin"/> by <see cref="ModelsPlugin"/> so that
/// last-registration semantics in <see cref="AssetServer.RegisterLoader{T}"/> route those
/// extensions here.
/// </para>
/// </remarks>
/// <seealso cref="ModelsPlugin"/>
/// <seealso cref="GltfModelReader"/>
public sealed class GltfModelPlugin : IPlugin
{
    private static readonly ILogger Logger = Log.Category("Engine.Models.Gltf");

    /// <inheritdoc />
    public void Build(App app)
    {
        Logger.Info("GltfModelPlugin: Initialising SharpGLTF backend...");

        var reader = new GltfModelReader();
        var loader = new GltfModelLoader(reader);

        if (!app.World.TryGetResource<SceneReaderRegistry>(out var registry))
        {
            registry = new SceneReaderRegistry();
            app.World.InsertResource(registry);
            Logger.Warn("GltfModelPlugin: SceneReaderRegistry was missing - did you forget to add ScenesPlugin? Created one implicitly.");
        }
        registry.RegisterReader(reader);

        if (app.World.TryGetResource<AssetServer>(out var server))
        {
            server.RegisterLoader(loader);
            Logger.Debug("GltfModelPlugin: GltfModelLoader registered with AssetServer.");
        }
        else
        {
            Logger.Warn("GltfModelPlugin: AssetServer not found - GltfModelLoader was NOT registered. Add AssetPlugin first.");
        }

        Logger.Info("GltfModelPlugin: SharpGLTF backend ready.");
    }
}