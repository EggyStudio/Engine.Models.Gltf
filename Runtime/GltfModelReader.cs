using System.Numerics;
using GLTF = SharpGLTF.Schema2;

namespace Engine;

/// <summary>
/// <see cref="ISceneReader"/> for glTF 2.0 (<c>.gltf</c> + <c>.glb</c>) using the
/// SharpGLTF schema bindings. Produces the same <see cref="Scene"/> /
/// <see cref="SceneNode"/> shape as <see cref="UsdSceneReader"/> and
/// <see cref="AssimpModelReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated backend over Assimp's glTF path:</b> SharpGLTF reads the JSON
/// schema directly and preserves the PBR material graph, KHR_* extensions, animation
/// interpolation modes (STEP / LINEAR / CUBICSPLINE), morph targets, and skin inverse
/// bind matrices losslessly. Last-registration semantics in
/// <see cref="AssetServer.RegisterLoader{T}"/> ensure this loader wins for
/// <c>.gltf</c> / <c>.glb</c>.
/// </para>
/// <para>
/// <b>Coordinate / unit policy:</b> glTF mandates Y-up, right-handed, meters
/// (<see cref="SceneCoordinateSystem.YUp"/>, <see cref="Scene.SourceMetersPerUnit"/> = 1).
/// </para>
/// <para>
/// <b>Spool to temp file:</b> <see cref="GLTF.ModelRoot.Load"/> needs a path so it can
/// resolve sibling <c>.bin</c> buffer files and external textures. The reader spools the
/// asset stream to a temp file with the original extension and lets SharpGLTF discover
/// dependencies relative to that temp directory. <c>.glb</c> files are self-contained so
/// the temp-directory approach works for both flavours.
/// </para>
/// </remarks>
public sealed class GltfModelReader : ISceneReader
{
    private static readonly ILogger Logger = Log.Category("Engine.Models.Gltf");

    /// <inheritdoc />
    public string[] Extensions => [".gltf", ".glb"];

    /// <inheritdoc />
    public string FormatId => "gltf";

    /// <inheritdoc />
    public Task<Scene> ReadAsync(AssetLoadContext context, SceneImportSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        var tempPath = SpoolToTempFile(context);
        try
        {
            ct.ThrowIfCancellationRequested();
            var model = GLTF.ModelRoot.Load(tempPath, SharpGLTF.Validation.ValidationMode.TryFix);
            if (model is null)
                throw new InvalidOperationException($"GltfModelReader: ModelRoot.Load returned null for '{context.Path}'.");

            return Task.FromResult(BuildScene(model, context, settings, ct));
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    // -- ModelRoot → Scene --

    private static Scene BuildScene(GLTF.ModelRoot model, AssetLoadContext context, SceneImportSettings settings, CancellationToken ct)
    {
        var scene = new Scene
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(context.Path.Path),
            SourceCoordinateSystem = SceneCoordinateSystem.YUp, // glTF mandate
            SourceMetersPerUnit = 1.0,                          // glTF mandate
        };

        // Pre-pass: convert materials so each MeshPrimitive shares one payload by index.
        var materials = settings.LoadPayloads.HasFlag(LoadPayloads.Materials)
            ? BuildMaterials(model)
            : new Dictionary<int, SceneMaterialPayload>();

        // Pre-pass: convert skins so any node that references a skin by index can attach
        // the same skeleton payload.
        var skeletons = settings.LoadPayloads.HasFlag(LoadPayloads.Meshes)
            ? BuildSkeletons(model)
            : new Dictionary<int, SceneSkeletonPayload>();

        var defaultScene = model.DefaultScene ?? (model.LogicalScenes.Count > 0 ? model.LogicalScenes[0] : null);
        if (defaultScene is null)
        {
            Logger.Warn($"GltfModelReader: '{context.Path}' has no default scene; producing an empty Scene.");
            return scene;
        }

        foreach (var node in defaultScene.VisualChildren)
        {
            ct.ThrowIfCancellationRequested();
            var converted = ConvertNode(node, "/", model, materials, skeletons, settings, ct);
            if (converted is not null) scene.Roots.Add(converted);
        }

        // Animations attach to the first root so consumers can find them via Scene.Traverse.
        if (model.LogicalAnimations.Count > 0)
        {
            if (scene.Roots.Count == 0)
                scene.Roots.Add(new SceneNode { Name = "Root", SourcePath = "/" });
            foreach (var anim in model.LogicalAnimations)
            {
                ct.ThrowIfCancellationRequested();
                var clip = ConvertAnimation(anim);
                if (clip is not null) scene.Roots[0].Components.Add(clip);
            }
        }

        LogSummary(context, scene, materials.Count, model.LogicalMeshes.Count, skeletons.Count, model.LogicalAnimations.Count);
        return scene;
    }

    // -- Materials --

    private static Dictionary<int, SceneMaterialPayload> BuildMaterials(GLTF.ModelRoot model)
    {
        var dict = new Dictionary<int, SceneMaterialPayload>(model.LogicalMaterials.Count);
        foreach (var mat in model.LogicalMaterials)
        {
            var name = string.IsNullOrEmpty(mat.Name) ? $"Material_{mat.LogicalIndex}" : mat.Name;
            var sourcePath = $"/Materials/{name}#{mat.LogicalIndex}";

            // Channels: BaseColor, MetallicRoughness, Normal, Emissive, Occlusion.
            var baseChan = mat.FindChannel("BaseColor");
            var mrChan   = mat.FindChannel("MetallicRoughness");
            var normChan = mat.FindChannel("Normal");
            var emisChan = mat.FindChannel("Emissive");
            var occlChan = mat.FindChannel("Occlusion");

            var baseFactor = baseChan?.Color ?? Vector4.One;
            var emissiveFactor = emisChan?.Color is { } ec ? new Vector3(ec.X, ec.Y, ec.Z) : Vector3.Zero;
            // MaterialChannel.GetFactor returns the strongly-typed scalar parameter
            // (or the channel's default) - no manual Parameters walk required.
            float metallic = mrChan?.GetFactor("MetallicFactor") ?? 1f;
            float roughness = mrChan?.GetFactor("RoughnessFactor") ?? 1f;
            float normalScale = normChan?.GetFactor("NormalScale") ?? 1f;
            float occlusionStrength = occlChan?.GetFactor("OcclusionStrength") ?? 1f;

            var alphaMode = mat.Alpha switch
            {
                GLTF.AlphaMode.OPAQUE => SceneAlphaMode.Opaque,
                GLTF.AlphaMode.MASK   => SceneAlphaMode.Mask,
                GLTF.AlphaMode.BLEND  => SceneAlphaMode.Blend,
                _                     => SceneAlphaMode.Opaque,
            };

            dict[mat.LogicalIndex] = new SceneMaterialPayload
            {
                Name = name,
                SourcePath = sourcePath,
                BaseColorFactor = baseFactor,
                BaseColorTexture = ToTextureRef(baseChan),
                MetallicFactor = metallic,
                RoughnessFactor = roughness,
                MetallicRoughnessTexture = ToTextureRef(mrChan),
                NormalTexture = ToTextureRef(normChan),
                NormalScale = normalScale,
                EmissiveFactor = emissiveFactor,
                EmissiveTexture = ToTextureRef(emisChan),
                OcclusionTexture = ToTextureRef(occlChan),
                OcclusionStrength = occlusionStrength,
                AlphaMode = alphaMode,
                AlphaCutoff = mat.AlphaCutoff,
                DoubleSided = mat.DoubleSided,
            };
        }
        return dict;
    }

    private static SceneTextureRef? ToTextureRef(GLTF.MaterialChannel? channel)
    {
        if (channel is null) return null;
        var ch = channel.Value;
        var tex = ch.Texture;
        if (tex is null) return null;

        // Resolve a path-ish identifier for the texture: prefer the source URI of the
        // primary image; fall back to a synthetic name for embedded image data so the
        // texture loader can still distinguish it (the actual bytes will need a custom
        // resolver to read them out of the model in that case - tracked separately).
        string assetPath = tex.PrimaryImage?.Name
                           ?? $"image_{tex.PrimaryImage?.LogicalIndex ?? -1}.embedded";
        if (tex.PrimaryImage?.Content.SourcePath is { Length: > 0 } src)
            assetPath = src;

        var sampler = tex.Sampler;
        var wrapS = sampler is null ? SceneWrapMode.Repeat : ConvertWrap(sampler.WrapS);
        var wrapT = sampler is null ? SceneWrapMode.Repeat : ConvertWrap(sampler.WrapT);

        return new SceneTextureRef(assetPath, ch.TextureCoordinate, wrapS, wrapT);
    }

    private static SceneWrapMode ConvertWrap(GLTF.TextureWrapMode mode) => mode switch
    {
        GLTF.TextureWrapMode.REPEAT          => SceneWrapMode.Repeat,
        GLTF.TextureWrapMode.MIRRORED_REPEAT => SceneWrapMode.Mirror,
        GLTF.TextureWrapMode.CLAMP_TO_EDGE   => SceneWrapMode.Clamp,
        _                                    => SceneWrapMode.Repeat,
    };

    // -- Skeletons --

    private static Dictionary<int, SceneSkeletonPayload> BuildSkeletons(GLTF.ModelRoot model)
    {
        var dict = new Dictionary<int, SceneSkeletonPayload>(model.LogicalSkins.Count);
        foreach (var skin in model.LogicalSkins)
        {
            int n = skin.JointsCount;
            var names = new string[n];
            var ibms = new Matrix4x4[n];
            var parents = new int[n];

            // Joint -> array index lookup
            var nodeToIdx = new Dictionary<GLTF.Node, int>(n);
            for (int j = 0; j < n; j++)
            {
                var (joint, ibm) = skin.GetJoint(j);
                names[j] = string.IsNullOrEmpty(joint.Name) ? $"Joint_{j}" : joint.Name;
                ibms[j] = ibm;
                nodeToIdx[joint] = j;
                parents[j] = -1;
            }
            for (int j = 0; j < n; j++)
            {
                var (joint, _) = skin.GetJoint(j);
                var p = joint.VisualParent;
                while (p is not null)
                {
                    if (nodeToIdx.TryGetValue(p, out var pIdx)) { parents[j] = pIdx; break; }
                    p = p.VisualParent;
                }
            }

            dict[skin.LogicalIndex] = new SceneSkeletonPayload
            {
                Name = string.IsNullOrEmpty(skin.Name) ? $"Skin_{skin.LogicalIndex}" : skin.Name,
                JointNames = names,
                ParentIndices = parents,
                InverseBindMatrices = ibms,
            };
        }
        return dict;
    }

    // -- Nodes --

    private static SceneNode? ConvertNode(
        GLTF.Node gltfNode,
        string parentPath,
        GLTF.ModelRoot model,
        IReadOnlyDictionary<int, SceneMaterialPayload> materials,
        IReadOnlyDictionary<int, SceneSkeletonPayload> skeletons,
        SceneImportSettings settings,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var name = string.IsNullOrEmpty(gltfNode.Name) ? $"Node_{gltfNode.LogicalIndex}" : gltfNode.Name;
        var path = parentPath.EndsWith('/') ? parentPath + name : parentPath + "/" + name;

        var lt = gltfNode.LocalTransform;
        var node = new SceneNode
        {
            Name = name,
            SourcePath = path,
            LocalTransform = new Transform
            {
                Position = lt.Translation,
                Rotation = lt.Rotation,
                Scale = lt.Scale,
            },
        };

        // Mesh: convert each primitive into its own SceneMeshPayload (glTF allows N primitives
        // per mesh, each with its own material - mirrors how Assimp splits them).
        if (settings.LoadPayloads.HasFlag(LoadPayloads.Meshes) && gltfNode.Mesh is { } mesh)
        {
            for (int p = 0; p < mesh.Primitives.Count; p++)
            {
                var prim = mesh.Primitives[p];
                var payload = ConvertPrimitive(prim, $"{name}#{p}", materials);
                if (payload is null) continue;
                node.Components.Add(payload);

                if (settings.LoadPayloads.HasFlag(LoadPayloads.Materials)
                    && prim.Material is { } pmat
                    && materials.TryGetValue(pmat.LogicalIndex, out var matPayload))
                {
                    node.Components.Add(matPayload);
                }

                // Skinning lives on the gltfNode (Skin), not the primitive.
                if (gltfNode.Skin is { } skin
                    && skeletons.TryGetValue(skin.LogicalIndex, out var skel))
                {
                    var skinPayload = ConvertSkin(prim, skel);
                    if (skinPayload is not null)
                    {
                        node.Components.Add(skinPayload);
                        node.Components.Add(skel);
                    }
                }
            }
        }

        // Camera
        if (settings.LoadPayloads.HasFlag(LoadPayloads.Cameras) && gltfNode.Camera is { } cam)
            node.Components.Add(ConvertCamera(cam));

        // Punctual light (KHR_lights_punctual)
        if (settings.LoadPayloads.HasFlag(LoadPayloads.Lights) && gltfNode.PunctualLight is { } light)
            node.Components.Add(ConvertLight(light));

        // Children
        foreach (var child in gltfNode.VisualChildren)
        {
            var c = ConvertNode(child, path, model, materials, skeletons, settings, ct);
            if (c is not null) node.Children.Add(c);
        }

        return node;
    }

    // -- Mesh primitives --

    private static SceneMeshPayload? ConvertPrimitive(
        GLTF.MeshPrimitive prim,
        string name,
        IReadOnlyDictionary<int, SceneMaterialPayload> materials)
    {
        if (prim.DrawPrimitiveType != GLTF.PrimitiveType.TRIANGLES) return null;

        var posAcc = prim.GetVertexAccessor("POSITION");
        if (posAcc is null) return null;
        var positions = posAcc.AsVector3Array().ToArray();

        var idxAcc = prim.IndexAccessor;
        int[] indices;
        if (idxAcc is not null)
        {
            var idxArr = idxAcc.AsIndicesArray();
            indices = new int[idxArr.Count];
            for (int i = 0; i < idxArr.Count; i++) indices[i] = (int)idxArr[i];
        }
        else
        {
            // Non-indexed: synthesize a 0..n-1 index buffer.
            indices = new int[positions.Length];
            for (int i = 0; i < positions.Length; i++) indices[i] = i;
        }

        Vector3[]? normals = null;
        if (prim.GetVertexAccessor("NORMAL") is { } nAcc)
            normals = nAcc.AsVector3Array().ToArray();

        Vector4[]? tangents = null;
        if (prim.GetVertexAccessor("TANGENT") is { } tAcc)
            tangents = tAcc.AsVector4Array().ToArray();

        Vector2[]? uv0 = null, uv1 = null;
        if (prim.GetVertexAccessor("TEXCOORD_0") is { } uvAcc0) uv0 = uvAcc0.AsVector2Array().ToArray();
        if (prim.GetVertexAccessor("TEXCOORD_1") is { } uvAcc1) uv1 = uvAcc1.AsVector2Array().ToArray();

        Vector4[]? colors = null;
        if (prim.GetVertexAccessor("COLOR_0") is { } cAcc)
        {
            // glTF allows VEC3 or VEC4 colors; handle both.
            if (cAcc.Dimensions == GLTF.DimensionType.VEC4)
            {
                colors = cAcc.AsVector4Array().ToArray();
            }
            else
            {
                var v3 = cAcc.AsVector3Array();
                colors = new Vector4[v3.Count];
                for (int i = 0; i < v3.Count; i++) colors[i] = new Vector4(v3[i], 1f);
            }
        }

        IReadOnlyList<SceneMeshSubset> subsets = Array.Empty<SceneMeshSubset>();
        if (prim.Material is { } pmat && materials.TryGetValue(pmat.LogicalIndex, out var mat))
        {
            subsets = new[] { new SceneMeshSubset("__bound", 0, indices.Length, mat.SourcePath) };
        }

        return new SceneMeshPayload
        {
            Name = name,
            Positions = positions,
            Indices = indices,
            Normals = normals,
            Tangents = tangents,
            Uv0 = uv0,
            Uv1 = uv1,
            Colors = colors,
            Subsets = subsets,
            LocalBounds = SceneBounds.FromPositions(positions),
        };
    }

    private static SceneSkinPayload? ConvertSkin(GLTF.MeshPrimitive prim, SceneSkeletonPayload skel)
    {
        var jointsAcc = prim.GetVertexAccessor("JOINTS_0");
        var weightsAcc = prim.GetVertexAccessor("WEIGHTS_0");
        if (jointsAcc is null || weightsAcc is null) return null;

        var jointsArr = jointsAcc.AsVector4Array();
        var weightsArr = weightsAcc.AsVector4Array();
        if (jointsArr.Count != weightsArr.Count) return null;

        int vc = jointsArr.Count;
        var idx = new ushort[vc * 4];
        var wts = new float[vc * 4];
        for (int v = 0; v < vc; v++)
        {
            var j = jointsArr[v];
            var w = weightsArr[v];
            idx[v * 4]     = (ushort)j.X;
            idx[v * 4 + 1] = (ushort)j.Y;
            idx[v * 4 + 2] = (ushort)j.Z;
            idx[v * 4 + 3] = (ushort)j.W;
            float sum = w.X + w.Y + w.Z + w.W;
            float inv = sum > 0f ? 1f / sum : 0f;
            wts[v * 4]     = w.X * inv;
            wts[v * 4 + 1] = w.Y * inv;
            wts[v * 4 + 2] = w.Z * inv;
            wts[v * 4 + 3] = w.W * inv;
        }

        return new SceneSkinPayload
        {
            SkeletonPath = $"/Skeletons/{skel.Name}",
            JointIndices = idx,
            JointWeights = wts,
        };
    }

    // -- Cameras --

    private static SceneCameraPayload ConvertCamera(GLTF.Camera cam)
    {
        const float ReferenceVertAperture = 15.2908f;

        if (cam.Settings is GLTF.CameraPerspective persp)
        {
            float vfov = persp.VerticalFOV;
            float aspect = persp.AspectRatio ?? (16f / 9f);
            float focalLength = ReferenceVertAperture / (2f * MathF.Tan(vfov * 0.5f));
            return new SceneCameraPayload
            {
                Name = string.IsNullOrEmpty(cam.Name) ? "Camera" : cam.Name,
                Projection = SceneProjection.Perspective,
                HorizontalAperture = ReferenceVertAperture * aspect,
                VerticalAperture = ReferenceVertAperture,
                FocalLength = focalLength,
                NearClip = persp.ZNear,
                FarClip = persp.ZFar,
            };
        }
        if (cam.Settings is GLTF.CameraOrthographic ortho)
        {
            return new SceneCameraPayload
            {
                Name = string.IsNullOrEmpty(cam.Name) ? "Camera" : cam.Name,
                Projection = SceneProjection.Orthographic,
                HorizontalAperture = ortho.XMag * 2f,
                VerticalAperture = ortho.YMag * 2f,
                NearClip = ortho.ZNear,
                FarClip = ortho.ZFar,
            };
        }
        return new SceneCameraPayload { Name = cam.Name ?? "Camera" };
    }

    // -- Punctual lights --

    private static SceneLightPayload ConvertLight(GLTF.PunctualLight l)
    {
        return new SceneLightPayload
        {
            Name = string.IsNullOrEmpty(l.Name) ? "Light" : l.Name,
            Type = l.LightType switch
            {
                GLTF.PunctualLightType.Directional => SceneLightType.Distant,
                GLTF.PunctualLightType.Point       => SceneLightType.Sphere,
                GLTF.PunctualLightType.Spot        => SceneLightType.Sphere,
                _                                  => SceneLightType.Sphere,
            },
            Color = l.Color,
            Intensity = l.Intensity,
            ConeAngle = l.LightType == GLTF.PunctualLightType.Spot
                ? l.OuterConeAngle * (180f / MathF.PI)
                : null,
        };
    }

    // -- Animations --

    private static SceneAnimationPayload? ConvertAnimation(GLTF.Animation anim)
    {
        var channels = new List<SceneAnimationChannel>(anim.Channels.Count);
        float duration = 0f;

        foreach (var ch in anim.Channels)
        {
            var target = ch.TargetNode;
            if (target is null) continue;
            string targetPath = "/" + (string.IsNullOrEmpty(target.Name) ? $"Node_{target.LogicalIndex}" : target.Name);

            // Sampler interpolation
            SceneAnimationInterpolation interp = SceneAnimationInterpolation.Linear;

            // Property + sampler
            switch (ch.TargetNodePath)
            {
                case GLTF.PropertyPath.translation:
                    {
                        var s = ch.GetTranslationSampler();
                        if (s is null) continue;
                        interp = MapInterp(s.InterpolationMode);
                        var keys = s.GetLinearKeys().ToArray();
                        var times = new float[keys.Length];
                        var values = new Vector4[keys.Length];
                        for (int k = 0; k < keys.Length; k++)
                        {
                            times[k] = keys[k].Key;
                            var t = keys[k].Value;
                            values[k] = new Vector4(t.X, t.Y, t.Z, 0f);
                            if (times[k] > duration) duration = times[k];
                        }
                        channels.Add(new SceneAnimationChannel
                        {
                            TargetNodePath = targetPath,
                            Property = SceneAnimationProperty.Translation,
                            Interpolation = interp,
                            TimesSeconds = times,
                            Values = values,
                        });
                        break;
                    }
                case GLTF.PropertyPath.rotation:
                    {
                        var s = ch.GetRotationSampler();
                        if (s is null) continue;
                        interp = MapInterp(s.InterpolationMode);
                        var keys = s.GetLinearKeys().ToArray();
                        var times = new float[keys.Length];
                        var values = new Vector4[keys.Length];
                        for (int k = 0; k < keys.Length; k++)
                        {
                            times[k] = keys[k].Key;
                            var q = keys[k].Value;
                            values[k] = new Vector4(q.X, q.Y, q.Z, q.W);
                            if (times[k] > duration) duration = times[k];
                        }
                        channels.Add(new SceneAnimationChannel
                        {
                            TargetNodePath = targetPath,
                            Property = SceneAnimationProperty.Rotation,
                            Interpolation = interp,
                            TimesSeconds = times,
                            Values = values,
                        });
                        break;
                    }
                case GLTF.PropertyPath.scale:
                    {
                        var s = ch.GetScaleSampler();
                        if (s is null) continue;
                        interp = MapInterp(s.InterpolationMode);
                        var keys = s.GetLinearKeys().ToArray();
                        var times = new float[keys.Length];
                        var values = new Vector4[keys.Length];
                        for (int k = 0; k < keys.Length; k++)
                        {
                            times[k] = keys[k].Key;
                            var v = keys[k].Value;
                            values[k] = new Vector4(v.X, v.Y, v.Z, 0f);
                            if (times[k] > duration) duration = times[k];
                        }
                        channels.Add(new SceneAnimationChannel
                        {
                            TargetNodePath = targetPath,
                            Property = SceneAnimationProperty.Scale,
                            Interpolation = interp,
                            TimesSeconds = times,
                            Values = values,
                        });
                        break;
                    }
                // weights (morph targets) deferred until SceneMorphTargetsPayload lands.
            }
        }

        if (channels.Count == 0) return null;
        return new SceneAnimationPayload
        {
            Name = string.IsNullOrEmpty(anim.Name) ? "Animation" : anim.Name,
            DurationSeconds = duration,
            Channels = channels,
        };
    }

    private static SceneAnimationInterpolation MapInterp(GLTF.AnimationInterpolationMode mode) => mode switch
    {
        GLTF.AnimationInterpolationMode.STEP        => SceneAnimationInterpolation.Step,
        GLTF.AnimationInterpolationMode.LINEAR      => SceneAnimationInterpolation.Linear,
        GLTF.AnimationInterpolationMode.CUBICSPLINE => SceneAnimationInterpolation.CubicSpline,
        _                                           => SceneAnimationInterpolation.Linear,
    };

    private static string SpoolToTempFile(AssetLoadContext context)
    {
        var ext = context.Path.Extension;
        if (string.IsNullOrEmpty(ext)) ext = ".glb";
        // Use a per-load subdirectory so SharpGLTF's relative-URI resolution doesn't
        // collide with sibling temp files from concurrent loads.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gltfspool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var tempPath = System.IO.Path.Combine(dir, $"asset{ext}");

        var stream = context.GetStream();
        if (stream.CanSeek) stream.Position = 0;
        using (var file = File.Create(tempPath))
            stream.CopyTo(file);
        return tempPath;
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (File.Exists(path)) File.Delete(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch (Exception ex) { Logger.Debug($"GltfModelReader: failed to delete temp '{path}': {ex.Message}"); }
    }

    // -- Logging --

    private static void LogSummary(AssetLoadContext context, Scene scene, int mats, int meshes, int skels, int anims)
    {
        int nodes = 0, attached = 0;
        foreach (var r in scene.Roots) Tally(r);

        Logger.Info(
            $"GltfModelReader: '{context.Path}' parsed - roots={scene.Roots.Count}, nodes={nodes}, " +
            $"meshes={meshes}, materials={mats}, skeletons={skels}, animations={anims}, payloads={attached}.");

        void Tally(SceneNode n)
        {
            nodes++;
            attached += n.Components.Count;
            foreach (var c in n.Children) Tally(c);
        }
    }
}