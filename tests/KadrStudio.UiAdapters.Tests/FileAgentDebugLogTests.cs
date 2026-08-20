using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Services.Agent;

namespace KadrStudio.UiAdapters.Tests;

public sealed class FileAgentDebugLogTests
{
    [Fact]
    public void Session_log_is_written_and_preserves_unicode()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "KadrStudio-AgentDebugLogTests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var logger = new FileAgentDebugLog(root);

            logger.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "chat_ui",
                "user_message",
                Guid.NewGuid(),
                "Investigating",
                Message: "Не трогай первую минуту исходника.",
                Details: "тестовый контекст"));

            Assert.NotNull(logger.CurrentLogPath);
            var currentLogPath = logger.CurrentLogPath!;
            Assert.True(File.Exists(currentLogPath));

            var latestPathFile = Path.Combine(root, "latest-path.txt");
            Assert.True(File.Exists(latestPathFile));
            Assert.Equal(
                currentLogPath,
                File.ReadAllText(latestPathFile));

            var content = File.ReadAllText(currentLogPath);
            Assert.Contains(
                "Не трогай первую минуту исходника.",
                content,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"eventName\":\"user_message\"",
                content,
                StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
