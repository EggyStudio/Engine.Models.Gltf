namespace Engine;

/// <summary>
/// <see cref="IAssetLoader{T}"/> for glTF 2.0 (<c>.gltf</c> / <c>.glb</c>) using
/// SharpGLTF. Delegates the actual schema parsing to <see cref="GltfModelReader"/>.
/// </summary>
/// <seealso cref="GltfModelPlugin"/>
public sealed class GltfModelLoader : IAssetLoader<SceneAsset>
{
    private readonly GltfModelReader _reader;

    /// <summary>Creates a loader bound to the given reader.</summary>
    public GltfModelLoader(GltfModelReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <inheritdoc />
    public string[] Extensions => [".gltf", ".glb"];

    /// <inheritdoc />
    public async Task<AssetLoadResult<SceneAsset>> LoadAsync(AssetLoadContext context, CancellationToken ct)
    {
        try
        {
            var scene = await _reader.ReadAsync(context, SceneImportSettings.Default, ct);
            var asset = new SceneAsset
            {
                Scene = scene,
                SourcePath = context.Path.ToString(),
                SourceFormat = _reader.FormatId,
            };
            return AssetLoadResult<SceneAsset>.Ok(asset);
        }
        catch (Exception ex)
        {
            return AssetLoadResult<SceneAsset>.Fail($"glTF load failed for '{context.Path}': {ex.Message}");
        }
    }
}