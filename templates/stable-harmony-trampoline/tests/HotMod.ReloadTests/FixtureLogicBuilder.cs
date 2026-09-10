namespace HotMod.ReloadTests;

public static class FixtureLogicBuilder
{
    public static async Task<string> BuildAsync(string fixtureName)
    {
        var templateRoot = FindTemplateRoot();
        var projectPath = Path.Combine(templateRoot, "tests", "fixtures", fixtureName, $"{fixtureName}.csproj");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "hotmod-fixtures", fixtureName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = templateRoot
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputDirectory);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet build.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture build failed for {fixtureName}:{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }

        return Path.Combine(outputDirectory, $"{fixtureName}.dll");
    }

    private static string FindTemplateRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "HotMod.Template.sln")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find stable harmony template root.");
    }
}
