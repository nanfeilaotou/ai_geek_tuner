using System.Diagnostics;
using System.IO;
using System.Text;

namespace AIGeekTuner.Tests.TestSupport;

/// <summary>
/// 每个测试实例独立的临时目录（%TEMP% 下 GUID 命名），
/// 析构时 best-effort 删除；绝不触碰用户真实 AppData。
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private const string Prefix = "aigt-tests-";

    public TempDirectory()
    {
        FullPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    /// <summary>目录绝对路径。</summary>
    public string FullPath { get; }

    public string WriteFile(string fileName, byte[] bytes)
    {
        var fullPath = System.IO.Path.Combine(FullPath, fileName);
        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }

    public string Combine(string fileName) =>
        System.IO.Path.Combine(FullPath, fileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (Exception cleanupFailure)
        {
            // 清理失败不能让测试本身失败；留痕便于排查残留。
            Debug.WriteLine($"TempDirectory cleanup failed: {cleanupFailure.Message}");
        }
    }
}
