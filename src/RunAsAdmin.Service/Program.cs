using RunAsAdmin.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "RunAsAdmin");

#pragma warning disable CA1416 // Der Dienst ist bewusst Windows-only.
builder.Services.AddHostedService<Worker>();
#pragma warning restore CA1416

var host = builder.Build();
host.Run();
