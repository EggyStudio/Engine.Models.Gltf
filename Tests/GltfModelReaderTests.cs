using System.Numerics;
using FluentAssertions;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace Engine.Tests.Models.Gltf;

/// <summary>
/// Integration tests for the <see cref="GltfModelReader"/> backend. Builds a tiny
/// in-memory glb (single-triangle scene + default material) using SharpGLTF's
/// SceneBuilder API and feeds the bytes through the reader to verify the produced
/// <see cref="Scene"/> snapshot.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "SharpGLTF")]
public class GltfModelReaderTests
{
    private static byte[] MakeTriangleGlb()
    {
        var mesh = new MeshBuilder<VertexPosition>("triangle");
        var mat = MaterialBuilder.CreateDefault();
        mesh.UsePrimitive(mat).AddTriangle(
            new VertexPosition(0, 0, 0),
            new VertexPosition(1, 0, 0),
            new VertexPosition(0, 1, 0));

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);

        var model = scene.ToGltf2();
        return model.WriteGLB().ToArray();
    }

    private static AssetLoadContext OpenContext(string path, byte[] bytes) =>
        new AssetLoadContext(new MemoryStream(bytes), new AssetPath(path), _ => default);

    [Fact]
    public void Reader_Format_Id_And_Extensions()
    {
        var r = new GltfModelReader();
        r.FormatId.Should().Be("gltf");
        r.Extensions.Should().BeEquivalentTo(new[] { ".gltf", ".glb" });
    }

    [Fact]
    public async Task ReadAsync_Glb_Triangle_Produces_YUp_Meter_Scene_With_Mesh()
    {
        var reader = new GltfModelReader();
        using var ctx = OpenContext("tests/inline.glb", MakeTriangleGlb());

        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        scene.SourceCoordinateSystem.Should().Be(SceneCoordinateSystem.YUp);
        scene.SourceMetersPerUnit.Should().Be(1.0);
        scene.Roots.Should().NotBeEmpty();

        var meshes = new List<SceneMeshPayload>();
        void Walk(SceneNode n)
        {
            foreach (var c in n.Components)
                if (c is SceneMeshPayload m) meshes.Add(m);
            foreach (var ch in n.Children) Walk(ch);
        }
        foreach (var r in scene.Roots) Walk(r);

        meshes.Should().NotBeEmpty();
        meshes[0].Positions.Length.Should().Be(3);
        meshes[0].Indices.Length.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_Glb_Reports_Default_Material_Payload()
    {
        var reader = new GltfModelReader();
        using var ctx = OpenContext("tests/inline.glb", MakeTriangleGlb());

        var scene = await reader.ReadAsync(ctx, SceneImportSettings.Default, CancellationToken.None);

        var materials = new List<SceneMaterialPayload>();
        void Walk(SceneNode n)
        {
            foreach (var c in n.Components)
                if (c is SceneMaterialPayload m) materials.Add(m);
            foreach (var ch in n.Children) Walk(ch);
        }
        foreach (var r in scene.Roots) Walk(r);

        materials.Should().NotBeEmpty();
        // SharpGLTF's CreateDefault() builds an MR material with white base colour
        // and roughness=1, metallic=0.
        materials[0].BaseColorFactor.Should().Be(Vector4.One);
        materials[0].RoughnessFactor.Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public async Task ReadAsync_Honours_Cancellation()
    {
        var reader = new GltfModelReader();
        using var ctx = OpenContext("tests/inline.glb", MakeTriangleGlb());
        var ct = new CancellationToken(canceled: true);

        var act = () => reader.ReadAsync(ctx, SceneImportSettings.Default, ct);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Loader_Wraps_Reader_With_Same_Extensions()
    {
        var loader = new GltfModelLoader(new GltfModelReader());
        loader.Extensions.Should().BeEquivalentTo(new[] { ".gltf", ".glb" });
    }

    [Fact]
    public void Loader_Throws_On_Null_Reader()
    {
        var act = () => new GltfModelLoader(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}