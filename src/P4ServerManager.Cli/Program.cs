using P4ServerManager.Cli;

return await CliApp.BuildRootCommand(CliEnvironment.Create()).Parse(args).InvokeAsync();
