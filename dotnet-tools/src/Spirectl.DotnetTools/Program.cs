using Spirectl.DotnetTools;

var result = ToolCommandDispatcher.Dispatch(args);
Console.Write(result.Output);
Environment.Exit(result.ExitCode);

