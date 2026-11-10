using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr1;
using Taipower.Safety.Core.Reference;
using Taipower.Safety.Infrastructure.Data;
using Taipower.Safety.Infrastructure.Email;

namespace Taipower.Safety.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSafetyInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<StorageOptions>>().Value);
        services.AddSingleton<IFileStorage, FileStorage>();

        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IOrgRepository, OrgRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<IReferenceRepository, ReferenceRepository>();
        services.AddScoped<AuditLogRepository>();
        services.AddScoped<IAuditLog>(sp => sp.GetRequiredService<AuditLogRepository>());
        services.AddScoped<IAuditLogQuery>(sp => sp.GetRequiredService<AuditLogRepository>());
        services.AddScoped<IEmailSender, SmtpEmailSender>();

        services.AddScoped<IAssigneeResolver, AssigneeResolver>();
        services.AddScoped<IPr1Repository, Pr1Repository>();
        services.AddScoped(sp => new Pr1EmailComposer(sp.GetRequiredService<IReferenceRepository>())
        {
            PublicBaseUrl = () => sp.GetRequiredService<IOptions<EmailOptions>>().Value.PublicBaseUrl,
        });
        services.AddScoped<Pr1Service>();
        return services;
    }
}
