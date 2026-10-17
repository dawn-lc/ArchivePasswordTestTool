using System.Runtime.CompilerServices;

// 允许测试项目访问 internal 成员（如 ArchiveFormatDetector.DetectArchiveFormat、ArchiveContext.TryGetVolumePaths）
[assembly: InternalsVisibleTo("ArchivePasswordTestTool.Tests")]
