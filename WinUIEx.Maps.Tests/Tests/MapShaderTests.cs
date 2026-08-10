using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapShaderTests
{
    [TestMethod]
    public void RuntimeShadersCompile()
    {
        MapShaders.Validate();
    }
}
