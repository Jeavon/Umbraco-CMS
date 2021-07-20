using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Exceptions;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations.Install;
using Umbraco.Cms.Infrastructure.Migrations.Upgrade;
using Umbraco.Cms.Infrastructure.Runtime;
using Umbraco.Extensions;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core;

namespace Umbraco.Cms.Infrastructure.Install
{
    public class UnattendedUpgrader : INotificationAsyncHandler<RuntimeUnattendedUpgradeNotification>
    {
        private readonly IProfilingLogger _profilingLogger;
        private readonly IUmbracoVersion _umbracoVersion;
        private readonly DatabaseBuilder _databaseBuilder;
        private readonly IRuntimeState _runtimeState;
        private readonly PackageMigrationPlanCollection _packageMigrationPlans;
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly IScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public UnattendedUpgrader(
            IProfilingLogger profilingLogger,
            IUmbracoVersion umbracoVersion,
            DatabaseBuilder databaseBuilder,
            IRuntimeState runtimeState,
            PackageMigrationPlanCollection packageMigrationPlans,
            IMigrationPlanExecutor migrationPlanExecutor,
            IScopeProvider scopeProvider,
            IKeyValueService keyValueService)
        {
            _profilingLogger = profilingLogger ?? throw new ArgumentNullException(nameof(profilingLogger));
            _umbracoVersion = umbracoVersion ?? throw new ArgumentNullException(nameof(umbracoVersion));
            _databaseBuilder = databaseBuilder ?? throw new ArgumentNullException(nameof(databaseBuilder));
            _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
            _packageMigrationPlans = packageMigrationPlans;
            _migrationPlanExecutor = migrationPlanExecutor;
            _scopeProvider = scopeProvider;
            _keyValueService = keyValueService;
        }

        public Task HandleAsync(RuntimeUnattendedUpgradeNotification notification, CancellationToken cancellationToken)
        {
            if (_runtimeState.RunUnattendedBootLogic())
            {
                switch (_runtimeState.Reason)
                {
                    case RuntimeLevelReason.UpgradeMigrations:
                    {
                        var plan = new UmbracoPlan(_umbracoVersion);
                        using (_profilingLogger.TraceDuration<UnattendedUpgrader>(
                            "Starting unattended upgrade.",
                            "Unattended upgrade completed."))
                        {
                            DatabaseBuilder.Result result = _databaseBuilder.UpgradeSchemaAndData(plan);
                            if (result.Success == false)
                            {
                                var innerException = new UnattendedInstallException("An error occurred while running the unattended upgrade.\n" + result.Message);
                                _runtimeState.Configure(Core.RuntimeLevel.BootFailed, Core.RuntimeLevelReason.BootFailedOnException, innerException);
                                return Task.CompletedTask;
                            }

                            notification.UnattendedUpgradeResult = RuntimeUnattendedUpgradeNotification.UpgradeResult.CoreUpgradeComplete;
                        }
                    }
                    break;
                    case RuntimeLevelReason.UpgradePackageMigrations:
                    {
                        if (!_runtimeState.StartupState.TryGetValue(RuntimeState.PendingPacakgeMigrationsStateKey, out var pm)
                            || pm is not IReadOnlyList<string> pendingMigrations)
                        {
                            throw new InvalidOperationException($"The required key {RuntimeState.PendingPacakgeMigrationsStateKey} does not exist in startup state");
                        }
                        
                        if (pendingMigrations.Count == 0)
                        {
                            throw new InvalidOperationException("No pending migrations found but the runtime level reason is " + Core.RuntimeLevelReason.UpgradePackageMigrations);
                        }

                        var exceptions = new List<Exception>();
                        var packageMigrationsPlans = _packageMigrationPlans.ToDictionary(x => x.Name);

                        foreach (var migrationName in pendingMigrations)
                        {
                            if (!packageMigrationsPlans.TryGetValue(migrationName, out PackageMigrationPlan plan))
                            {
                                throw new InvalidOperationException("Cannot find package migration plan " + migrationName);
                            }

                            using (_profilingLogger.TraceDuration<UnattendedUpgrader>(
                                "Starting unattended package migration for " + migrationName,
                                "Unattended upgrade completed for " + migrationName))
                            {
                                var upgrader = new Upgrader(plan);

                                try
                                {
                                    upgrader.Execute(_migrationPlanExecutor, _scopeProvider, _keyValueService);
                                }
                                catch (Exception ex)
                                {
                                    exceptions.Add(new UnattendedInstallException("Unattended package migration failed for " + migrationName, ex));                                    
                                }
                            }
                        }

                        if (exceptions.Count > 0)
                        {
                            notification.UnattendedUpgradeResult = RuntimeUnattendedUpgradeNotification.UpgradeResult.HasErrors;
                            SetRuntimeErrors(exceptions);
                        }
                        else
                        {
                            notification.UnattendedUpgradeResult = RuntimeUnattendedUpgradeNotification.UpgradeResult.PackageMigrationComplete;
                        }
                    }
                    break;
                    default:
                        throw new InvalidOperationException("Invalid reason " + _runtimeState.Reason);
                }
            }
            return Task.CompletedTask;
        }

        private void SetRuntimeErrors(List<Exception> exception)
        {            
            Exception innerException;
            if (exception.Count == 1)
            {
                innerException = exception[0];
            }
            else
            {
                innerException = new AggregateException(exception);
            }

            _runtimeState.Configure(
                RuntimeLevel.BootFailed,
                RuntimeLevelReason.BootFailedOnException,
                innerException);
        }
    }
}
