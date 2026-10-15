using System;
using System.Drawing;
using System.IO;
using EveOPreview.Mirrors;
using Serilog.Core;
using Xunit;

namespace EveOPreview.Tests.Checks;

public class MirrorTests
{
    [Fact]
    public void MirrorsRoundTripPerProfileWithoutComputedFields()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eveo-mirrors-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MirrorStore(Logger.None);
            Assert.Empty(store.Load(folder));
            var mirror = new MirrorDefinition
            {
                Name = "Local", SourceTitle = "EVE - Pilot One", SourceX = 20, SourceY = 40, SourceWidth = 480, SourceHeight = 160,
                X = -300, Y = 568, Width = 240, Height = 80, Opacity = 0.8,
            };
            store.Save(folder, [mirror]);
            string json = File.ReadAllText(Path.Combine(folder, MirrorStore.FileName));
            Assert.DoesNotContain("\"Zoom\"", json);
            Assert.DoesNotContain("\"Character\"", json);
            var loaded = Assert.Single(store.Load(folder));
            Assert.Equal(mirror, loaded);
            Assert.Equal("Pilot One", loaded.Character);
            Assert.Equal(0.5, loaded.Zoom);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void InvalidOrDamagedMirrorFilesLoadAsEmpty()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eveo-mirrors-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, MirrorStore.FileName);
            File.WriteAllText(path, """[{"SourceTitle":"","SourceWidth":100,"SourceHeight":100},{"SourceTitle":"EVE - A","SourceWidth":2,"SourceHeight":100}]""");
            Assert.Empty(new MirrorStore(Logger.None).Load(folder));
            File.WriteAllText(path, "not json");
            Assert.Empty(new MirrorStore(Logger.None).Load(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ZoomKeepsTheSourceAspectRatio()
    {
        var mirror = new MirrorDefinition { SourceTitle = "EVE - A", SourceWidth = 300, SourceHeight = 200, Width = 300, Height = 200 };
        var zoomed = mirror.WithZoom(1.5);
        Assert.Equal((450, 300), (zoomed.Width, zoomed.Height));
        Assert.Equal((16, 16), (mirror.WithZoom(0.01).Width, mirror.WithZoom(0.01).Height));
    }

    [Fact]
    public void EdgesAreDetectedAtTheBorderOnly()
    {
        Assert.Equal(MirrorGeometry.Edges.None, MirrorGeometry.HitEdges(50, 50, 200, 100, 10));
        Assert.Equal(MirrorGeometry.Edges.Left | MirrorGeometry.Edges.Top, MirrorGeometry.HitEdges(2, 3, 200, 100, 10));
        Assert.Equal(MirrorGeometry.Edges.Right | MirrorGeometry.Edges.Bottom, MirrorGeometry.HitEdges(195, 95, 200, 100, 10));
    }

    [Fact]
    public void ResizeKeepsAspectAndAnchorsTheOppositeEdges()
    {
        var start = new Rectangle(100, 100, 300, 200);
        var right = MirrorGeometry.Resize(start, MirrorGeometry.Edges.Right, 60, 0, 1.5);
        Assert.Equal(new Rectangle(100, 100, 360, 240), right);
        var topLeft = MirrorGeometry.Resize(start, MirrorGeometry.Edges.Left | MirrorGeometry.Edges.Top, 30, 30, 1.5);
        Assert.Equal(start.Right, topLeft.Right);
        Assert.Equal(start.Bottom, topLeft.Bottom);
        Assert.Equal(1.5, (double)topLeft.Width / topLeft.Height, 2);
        var bottom = MirrorGeometry.Resize(start, MirrorGeometry.Edges.Bottom, 0, -1000, 1.5);
        Assert.Equal(32, bottom.Width);
        Assert.Equal((start.X, start.Y), (bottom.X, bottom.Y));
    }
}
