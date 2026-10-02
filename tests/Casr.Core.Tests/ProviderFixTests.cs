using System;
using System.IO;
using System.Text.Json;
using Casr.Core.Models;
using Xunit;

namespace Casr.Core.Tests;

public class ProviderFixTests : IDisposable
{
    private readonly string _tempDir;

    public ProviderFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_fixtests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void FlattenContent_Object_ReturnsTextProperty()
    {
        using var doc = JsonDocument.Parse("{\"text\":\"hello world\"}");
        Assert.Equal("hello world", ModelHelpers.FlattenContent(doc.RootElement));
    }

    [Fact]
    public void FlattenContent_Object_RecursesIntoContent()
    {
        using var doc = JsonDocument.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"inner\"}]}");
        Assert.Equal("inner", ModelHelpers.FlattenContent(doc.RootElement));
    }

    [Fact]
    public void FlattenContent_Object_RecursesIntoParts()
    {
        using var doc = JsonDocument.Parse("{\"parts\":[{\"text\":\"part one\"},{\"text\":\"part two\"}]}");
        Assert.Equal("part one\npart two", ModelHelpers.FlattenContent(doc.RootElement));
    }

    [Fact]
    public void FlattenContent_Object_ConcatenatesChildTextMembers()
    {
        using var doc = JsonDocument.Parse("{\"a\":{\"text\":\"first\"},\"b\":[{\"text\":\"second\"}]}");
        Assert.Equal("first\nsecond", ModelHelpers.FlattenContent(doc.RootElement));
    }
}
