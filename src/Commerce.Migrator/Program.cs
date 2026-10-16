using Commerce.Migrator;

return MigrationRunner.Run(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error);
