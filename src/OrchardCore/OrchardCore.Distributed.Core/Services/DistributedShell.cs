using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OrchardCore.DeferredTasks;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Models;
using OrchardCore.Hosting.ShellBuilders;

namespace OrchardCore.Distributed.Core.Services
{
    /// <summary>
    /// In a distributed environment, allows to synchronize the tenant states by subscribing
    /// to the 'Shell' channel, and then by publishing and reacting to shell event messages.
    /// </summary>
    public class DistributedShell : IShellEvents
    {
        private readonly IShellHost _shellHost;
        private readonly ShellSettings _shellSettings;
        private readonly IShellSettingsManager _shellSettingsManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMessageBus _messageBus;
        private readonly SemaphoreSlim _synLock;
        private bool _initialized;

        public DistributedShell(
            IShellHost shellHost,
            ShellSettings shellSettings,
            IShellSettingsManager shellSettingsManager,
            IHttpContextAccessor httpContextAccessor,
            IEnumerable<IMessageBus> _messageBuses)
        {
            _shellHost = shellHost;
            _shellSettings = shellSettings;
            _shellSettingsManager = shellSettingsManager;
            _httpContextAccessor = httpContextAccessor;

            if (shellSettings.Name == ShellHelper.DefaultShellName)
            {
                _messageBus = _messageBuses.LastOrDefault();
                _synLock = new SemaphoreSlim(1);
            }
        }

        /// <summary>
        /// Invoked when the 'Default' tenant has been created. Used to subscribe
        /// to the 'Shell' channel and react to events coming from other instances.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_messageBus == null)
            {
                return;
            }

            if (!_initialized)
            {
                await _synLock.WaitAsync();

                try
                {
                    if (!_initialized)
                    {
                        // Subscribe to the 'Shell' channel.
                        await _messageBus.SubscribeAsync("Shell", (channel, message) =>
                        {
                            var tokens = message.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

                            // Validate the message {tenant}:{event}[:{settings}]
                            if (tokens.Length < 2)
                            {
                                return;
                            }

                            // Try to load the last settings of the specified tenant.
                            _shellSettingsManager.TryLoadSettings(tokens[0], out var settings);

                            if (_httpContextAccessor.HttpContext == null)
                            {
                                _httpContextAccessor.HttpContext = new DefaultHttpContext();
                            }

                            if (tokens[1] == "Reload" && tokens.Length > 2)
                            {
                                ShellSettings publishedSettings = null;

                                try
                                {
                                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(tokens[2]));
                                    publishedSettings = JObject.Parse(json).ToObject<ShellSettings>();
                                }
                                catch { }

                                // Settings may not be in sync on a non shared storage.
                                if (publishedSettings != null && !publishedSettings.Equals(settings))
                                {
                                    try
                                    {
                                        // Sync the tenant settings.
                                        settings = publishedSettings;
                                        _shellSettingsManager.SaveSettings(publishedSettings);
                                    }
                                    catch { }
                                }

                                if (settings == null)
                                {
                                    return;
                                }

                                // Reload the shell of the specified tenant.
                                _shellHost.ReloadShellContextAsync(settings).GetAwaiter().GetResult();

                                // If the default shell has been reloaded.
                                if (settings.Name == ShellHelper.DefaultShellName)
                                {
                                    // Invoke the 'Initialize' event to subscribe again to the 'Shell' channel.
                                    _shellHost.ShellEventAsync(e => e.InitializeAsync()).GetAwaiter().GetResult();
                                }
                            }

                            else if (tokens[1] == "Initialize" && settings != null)
                            {
                                // Set the tenant state to "Initializing" waiting for a new 'Reload' event.
                                using (var scope = _shellHost.GetScopeAsync(settings).GetAwaiter().GetResult())
                                {
                                    var shellSettings = scope.ServiceProvider.GetService<ShellSettings>();
                                    shellSettings.State = TenantState.Initializing;
                                }
                            }
                        });
                    }
                }

                finally
                {
                    _initialized = true;
                    _synLock.Release();
                }
            }
        }

        /// <summary>
        /// Invoked before any tenant run a setup or recipe.
        /// </summary>
        public Task InitializeAsync(string tenant)
        {
            // Publish the shell 'Initialize' event message.
            return (_messageBus?.PublishAsync("Shell", tenant + ":Initialize") ?? Task.CompletedTask);
        }

        /// <summary>
        /// Invoked when any tenant is going to be reloaded.
        /// </summary>
        public Task ReloadAsync(string tenant)
        {
            // Check if no message bus but not for the 'Default' tenant for which a
            // message bus may have been just enabled, so let's use a deferred task.
            if (_messageBus == null && ShellHelper.DefaultShellName != tenant)
            {
                // Nothing to do.
                return Task.CompletedTask;
            }

            // No shell context feature if we are executing a 'Shell' event handler.
            if (_httpContextAccessor.HttpContext.Features.Get<ShellContext>() == null)
            {
                // Break the loop.
                return Task.CompletedTask;
            }

            // Check if the tenant is being initialized.
            if (!_shellHost.TryGetSettings(tenant, out var settings) || settings.State == TenantState.Initializing)
            {
                // Wait for a 'Reload'.
                return Task.CompletedTask;
            }

            // Use a deferred task to let any persistent storage be completed.
            var deferredTaskEngine = _httpContextAccessor.HttpContext.RequestServices.GetService<IDeferredTaskEngine>();

            deferredTaskEngine?.AddTask(async context =>
            {
                // Shell events are always published using the 'Default' tenant.
                using (var scope = await _shellHost.GetScopeAsync(ShellHelper.DefaultShellName))
                {
                    // If the default shell has been reloaded.
                    if (tenant == ShellHelper.DefaultShellName)
                    {
                        // Invoke the 'Initialize' event to subscribe again to the 'Shell' channel.
                        await _shellHost.ShellEventAsync(e => e.InitializeAsync());
                    }

                    if (_shellHost.TryGetSettings(tenant, out var tenantSettings))
                    {
                        var json = JObject.FromObject(tenantSettings).ToString(Formatting.None);
                        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

                        // Publish the shell 'Reload' event message.
                        var messageBus = scope.ServiceProvider.GetService<IMessageBus>();
                        await (messageBus?.PublishAsync("Shell", tenant + ":Reload:" + encoded) ?? Task.CompletedTask);
                    }
                }
            }, order: 100);

            return Task.CompletedTask;
        }
    }
}