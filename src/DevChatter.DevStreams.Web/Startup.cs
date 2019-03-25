using Dapper;
using DevChatter.DevStreams.Core.Data;
using DevChatter.DevStreams.Core.Services;
using DevChatter.DevStreams.Core.Settings;
using DevChatter.DevStreams.Infra.Dapper;
using DevChatter.DevStreams.Infra.Dapper.Services;
using DevChatter.DevStreams.Infra.Dapper.TypeHandlers;
using DevChatter.DevStreams.Infra.Db.Migrations;
using DevChatter.DevStreams.Infra.Twitch;
using DevChatter.DevStreams.Web.Data;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using System;
using System.Linq;
using System.Threading.Tasks;
using Essenbee.Alexa.Lib.Middleware;
using Essenbee.Alexa.Lib.HttpClients;
using Essenbee.Alexa.Lib.Interfaces;

namespace DevChatter.DevStreams.Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            var secrets = GetSecrets();
            Configuration["SkillId"] = secrets.appid;
            Configuration["ConnectionStrings:DefaultConnection"] = secrets.connStr;
            Configuration["TwitchSettings:ClientId"] = secrets.clientid;

            services.AddHttpClient<IAlexaClient, AlexaClient>();

            services.Configure<DatabaseSettings>(
                Configuration.GetSection("ConnectionStrings"));

            services.Configure<TwitchSettings>(
                Configuration.GetSection("TwitchSettings"));

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer(
                    Configuration.GetConnectionString("DefaultConnection")));

            services.AddDefaultIdentity<IdentityUser>(options =>
                {
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                    options.Lockout.MaxFailedAccessAttempts = 5;
                    options.Lockout.AllowedForNewUsers = true;

                    options.Password.RequireDigit = false;
                    options.Password.RequireLowercase = false;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Password.RequireUppercase = false;
                    options.Password.RequiredLength = 8;
                    options.Password.RequiredUniqueChars = 1;

                    options.SignIn.RequireConfirmedEmail = false;
                    options.SignIn.RequireConfirmedPhoneNumber = false;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            services.AddScoped<IUserClaimsPrincipalFactory<IdentityUser>, UserClaimsPrincipalFactory<IdentityUser, IdentityRole>>();

            services.AddFluentMigratorCore()
                .ConfigureRunner(
                    builder => builder
                        .AddSqlServer()
                        .WithGlobalConnectionString(Configuration.GetConnectionString("DefaultConnection"))
                        .ScanIn(typeof(CreateTagsTable).Assembly).For.Migrations());

            SqlMapper.AddTypeHandler(InstantHandler.Default);
            SqlMapper.AddTypeHandler(LocalTimeHandler.Default);

            services.AddScoped<IStreamSessionService, DapperSessionLookup>();
            services.AddScoped<IScheduledStreamService, ScheduledStreamService>();
            services.AddTransient<ITagSearchService, TagSearchService>();
            services.AddTransient<ICrudRepository, DapperCrudRepository>();
            services.AddTransient<IChannelSearchService, ChannelSearchService>();
            services.AddTransient<IChannelAggregateService, ChannelAggregateService>();
            services.AddTransient<ITwitchService, TwitchService>();

            services.AddSingleton<IClock>(SystemClock.Instance);

            services.AddTransient<IChannelPermissionsService,
                ChannelPermissionsService>();

            services
                .AddMvc()
                .AddRazorPagesOptions(options =>
                {
                    options.Conventions.AuthorizeFolder("/My");
                    options.Conventions.AuthorizeFolder("/Manage",
                        "RequireAdministratorRole");
                })
                .SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

            services.AddAuthorization(options =>
            {
                options.AddPolicy("RequireAdministratorRole",
                    policy => policy.RequireRole("Administrator"));
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, 
            IMigrationRunner migrationRunner, UserManager<IdentityUser> userManager, RoleManager<IdentityRole> roleManager)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseDatabaseErrorPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseCookiePolicy();

            InitializeDatabase(app, migrationRunner);

            SetUpDefaultUsersAndRoles(userManager, roleManager).Wait();

            app.UseAuthentication();

            app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/alexa"), (appBuilder) =>
            {
                appBuilder.UseAlexaRequestValidation();
            });

            app.UseMvc();

        }

        private async Task SetUpDefaultUsersAndRoles(UserManager<IdentityUser> userManager,
            RoleManager<IdentityRole> roleManager)
        {
            const string roleName = "Administrator";
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var identityRole = new IdentityRole(roleName);
                var roleCreateResult = await roleManager.CreateAsync(identityRole);
            }

            const string defaultUserAccountName = "chatter1@example.com"; // TODO: Pull from Config
            const string defaultUserPassword = "Passw0rd!"; // TODO: Pull from Config
            var usersInRole = (await userManager.GetUsersInRoleAsync(roleName));
            if (!usersInRole.Any() 
                && await userManager.FindByEmailAsync(defaultUserAccountName) == null)
            {
                var user = new IdentityUser(defaultUserAccountName);
                user.Email = defaultUserAccountName;
                
                var result = await userManager.CreateAsync(user, defaultUserPassword);

                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(user, roleName);
                }
            }
        }

        private void InitializeDatabase(IApplicationBuilder app, IMigrationRunner migrationRunner)
        {
            using (var scope = app.ApplicationServices.GetService<IServiceScopeFactory>().CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();
            }
            migrationRunner.MigrateUp();
        }

        private (string appid, string connStr, string clientid) GetSecrets()
        {
            var retries = 0;
            var retry = false;

            AzureServiceTokenProvider azureServiceTokenProvider = new AzureServiceTokenProvider();
            KeyVaultClient keyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback));

            do
            {
                var waitTime = Math.Min(GetWaitTime(retries), 2000000);
                System.Threading.Thread.Sleep(waitTime);

                try
                {
                    var skillAppId = keyVaultClient
                        .GetSecretAsync("https://codebasealphakeys.vault.azure.net/secrets/DevStreamsAppId/de4526409e184b439ab110198a4021d4")
                        .Result;
                    var devStreamsDb = keyVaultClient
                        .GetSecretAsync("https://codebasealphakeys.vault.azure.net/secrets/DevStreamsDb/c5dc706283d241a2bbc6c0c8b713d8c7")
                        .Result;
                    var twitchClientId = keyVaultClient
                        .GetSecretAsync("https://codebasealphakeys.vault.azure.net/secrets/TwitchClientid/8bc3e3ff4f5b4fe2b908c3096ceed936")
                        .Result;
                    return (skillAppId.Value, devStreamsDb.Value, twitchClientId.Value);
                }
                catch (KeyVaultErrorException keyVaultException)
                {
                    if ((int)keyVaultException.Response.StatusCode == 429)
                    {
                        retry = true;
                        retries++;
                    }
                }
            }
            while (retry && (retries++ < 10));

            return (string.Empty, string.Empty, string.Empty);
        }

        // This method implements exponential backoff if there are 429 errors from Azure Key Vault
        private static int GetWaitTime(int retryCount) => retryCount > 0 ? ((int)Math.Pow(2, retryCount) * 100) : 0;
    }
}
