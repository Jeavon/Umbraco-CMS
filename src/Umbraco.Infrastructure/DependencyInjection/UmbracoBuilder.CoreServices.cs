using System.Runtime.InteropServices;
using Examine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Handlers;
using Umbraco.Cms.Core.HealthChecks.NotificationMethods;
using Umbraco.Cms.Core.Hosting;
using Umbraco.Cms.Core.Install;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Logging.Serilog.Enrichers;
using Umbraco.Cms.Core.Logging.Viewer;
using Umbraco.Cms.Core.Mail;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Media;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.PropertyEditors.ValueConverters;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Runtime;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Templates;
using Umbraco.Cms.Core.Trees;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Examine;
using Umbraco.Cms.Infrastructure.HealthChecks;
using Umbraco.Cms.Infrastructure.HostedServices;
using Umbraco.Cms.Infrastructure.Install;
using Umbraco.Cms.Infrastructure.Media;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Install;
using Umbraco.Cms.Infrastructure.Migrations.PostMigrations;
using Umbraco.Cms.Infrastructure.Packaging;
using Umbraco.Cms.Infrastructure.Migrations.Upgrade.V_8_0_0.DataTypes;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Persistence.Mappers;
using Umbraco.Cms.Infrastructure.Runtime;
using Umbraco.Cms.Infrastructure.Search;
using Umbraco.Cms.Infrastructure.Serialization;
using Umbraco.Extensions;

namespace Umbraco.Cms.Infrastructure.DependencyInjection
{
    public static partial class UmbracoBuilderExtensions
    {
        /// <summary>
        /// Adds all core Umbraco services required to run which may be replaced later in the pipeline
        /// </summary>
        public static IUmbracoBuilder AddCoreInitialServices(this IUmbracoBuilder builder)
        {
            builder
                .AddMainDom()
                .AddLogging();

            builder.Services.AddUnique<IUmbracoDatabaseFactory, UmbracoDatabaseFactory>();
            builder.Services.AddUnique(factory => factory.GetRequiredService<IUmbracoDatabaseFactory>().CreateDatabase());
            builder.Services.AddUnique(factory => factory.GetRequiredService<IUmbracoDatabaseFactory>().SqlContext);
            builder.NPocoMappers().Add<NullableDateMapper>();
            builder.PackageMigrationPlans().Add(() => builder.TypeLoader.GetPackageMigrationPlans());

            builder.Services.AddUnique<IRuntimeState, RuntimeState>();
            builder.Services.AddUnique<IRuntime, CoreRuntime>();
            builder.Services.AddUnique<PendingPackageMigrations>();
            builder.AddNotificationAsyncHandler<RuntimeUnattendedInstallNotification, UnattendedInstaller>();
            builder.AddNotificationAsyncHandler<RuntimeUnattendedUpgradeNotification, UnattendedUpgrader>();

            // composers
            builder
                .AddRepositories()
                .AddServices()
                .AddCoreMappingProfiles()
                .AddFileSystems()
                .AddWebAssets();

            // register persistence mappers - required by database factory so needs to be done here
            // means the only place the collection can be modified is in a runtime - afterwards it
            // has been frozen and it is too late
            builder.Mappers().AddCoreMappers();

            // register the scope provider
            builder.Services.AddUnique<ScopeProvider>(); // implements both IScopeProvider and IScopeAccessor
            builder.Services.AddUnique<IScopeProvider>(f => f.GetRequiredService<ScopeProvider>());
            builder.Services.AddUnique<IScopeAccessor>(f => f.GetRequiredService<ScopeProvider>());
            builder.Services.AddScoped<IHttpScopeReference, HttpScopeReference>();

            builder.Services.AddUnique<IJsonSerializer, JsonNetSerializer>();
            builder.Services.AddUnique<IConfigurationEditorJsonSerializer, ConfigurationEditorJsonSerializer>();
            builder.Services.AddUnique<IMenuItemCollectionFactory, MenuItemCollectionFactory>();

            // register database builder
            // *not* a singleton, don't want to keep it around
            builder.Services.AddTransient<DatabaseBuilder>();

            // register manifest parser, will be injected in collection builders where needed
            builder.Services.AddUnique<IManifestParser, ManifestParser>();

            // register the manifest filter collection builder (collection is empty by default)
            builder.ManifestFilters();

            builder.MediaUrlGenerators()
                .Add<FileUploadPropertyEditor>()
                .Add<ImageCropperPropertyEditor>();

            builder.Services.AddUnique<IPublishedContentTypeFactory, PublishedContentTypeFactory>();

            builder.Services.AddUnique<IShortStringHelper>(factory
                => new DefaultShortStringHelper(new DefaultShortStringHelperConfig().WithDefault(factory.GetRequiredService<IOptions<RequestHandlerSettings>>().Value)));

            builder.Services.AddUnique<IMigrationPlanExecutor, MigrationPlanExecutor>();
            builder.Services.AddUnique<IMigrationBuilder>(factory => new MigrationBuilder(factory));

            builder.AddPreValueMigrators();

            builder.Services.AddUnique<IPublishedSnapshotRebuilder, PublishedSnapshotRebuilder>();

            // register the published snapshot accessor - the "current" published snapshot is in the umbraco context
            builder.Services.AddUnique<IPublishedSnapshotAccessor, UmbracoContextPublishedSnapshotAccessor>();

            builder.Services.AddUnique<IVariationContextAccessor, HybridVariationContextAccessor>();

            // Config manipulator
            builder.Services.AddUnique<IConfigManipulator, JsonConfigManipulator>();

            builder.Services.AddUnique<RichTextEditorPastedImages>();
            builder.Services.AddUnique<BlockEditorConverter>();

            // both TinyMceValueConverter (in Core) and RteMacroRenderingValueConverter (in Web) will be
            // discovered when CoreBootManager configures the converters. We will remove the basic one defined
            // in core so that the more enhanced version is active.
            builder.PropertyValueConverters()
                .Remove<SimpleTinyMceValueConverter>();

            builder.Services.AddUnique<IImageUrlGenerator, ImageSharpImageUrlGenerator>();

            // register *all* checks, except those marked [HideFromTypeFinder] of course
            builder.Services.AddUnique<IMarkdownToHtmlConverter, MarkdownToHtmlConverter>();

            builder.Services.AddUnique<IContentLastChanceFinder, ContentFinderByConfigured404>();

            builder.Services.AddScoped<UmbracoTreeSearcher>();

            // replace
            builder.Services.AddUnique<IEmailSender, EmailSender>();

            builder.Services.AddUnique<IExamineManager, ExamineManager>();

            builder.Services.AddScoped<ITagQuery, TagQuery>();

            builder.Services.AddUnique<IUmbracoTreeSearcherFields, UmbracoTreeSearcherFields>();
            builder.Services.AddScoped<IPublishedContentQuery>(factory =>
            {
                var umbCtx = factory.GetRequiredService<IUmbracoContextAccessor>();
                return new PublishedContentQuery(umbCtx.UmbracoContext.PublishedSnapshot, factory.GetRequiredService<IVariationContextAccessor>(), factory.GetRequiredService<IExamineManager>());
            });

            // register accessors for cultures
            builder.Services.AddUnique<IDefaultCultureAccessor, DefaultCultureAccessor>();

            builder.Services.AddSingleton<IFilePermissionHelper, FilePermissionHelper>();

            builder.Services.AddUnique<IUmbracoComponentRenderer, UmbracoComponentRenderer>();

            builder.Services.AddUnique<IBackOfficeExamineSearcher, NoopBackOfficeExamineSearcher>();

            builder.Services.AddUnique<UploadAutoFillProperties>();

            builder.Services.AddUnique<ICronTabParser, NCronTabParser>();

            builder.Services.AddUnique<IImageDimensionExtractor, ImageDimensionExtractor>();

            builder.Services.AddUnique<PackageDataInstallation>();

            builder.AddInstaller();

            // Services required to run background jobs (with out the handler)
            builder.Services.AddUnique<IBackgroundTaskQueue, BackgroundTaskQueue>();

            return builder;
        }

        /// <summary>
        /// Adds logging requirements for Umbraco
        /// </summary>
        private static IUmbracoBuilder AddLogging(this IUmbracoBuilder builder)
        {
            builder.Services.AddUnique<ThreadAbortExceptionEnricher>();
            builder.Services.AddUnique<HttpSessionIdEnricher>();
            builder.Services.AddUnique<HttpRequestNumberEnricher>();
            builder.Services.AddUnique<HttpRequestIdEnricher>();
            return builder;
        }

        private static IUmbracoBuilder AddMainDom(this IUmbracoBuilder builder)
        {
            builder.Services.AddUnique<IMainDomLock>(factory =>
            {
                var globalSettings = factory.GetRequiredService<IOptions<GlobalSettings>>();
                var connectionStrings = factory.GetRequiredService<IOptions<ConnectionStrings>>();
                var hostingEnvironment = factory.GetRequiredService<IHostingEnvironment>();

                var dbCreator = factory.GetRequiredService<IDbProviderFactoryCreator>();
                var databaseSchemaCreatorFactory = factory.GetRequiredService<DatabaseSchemaCreatorFactory>();
                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var loggerFactory = factory.GetRequiredService<ILoggerFactory>();
                var npocoMappers = factory.GetRequiredService<NPocoMapperCollection>();

                return globalSettings.Value.MainDomLock.Equals("SqlMainDomLock") || isWindows == false
                    ? (IMainDomLock)new SqlMainDomLock(
                            loggerFactory.CreateLogger<SqlMainDomLock>(),
                            loggerFactory,
                            globalSettings,
                            connectionStrings,
                            dbCreator,
                            hostingEnvironment,
                            databaseSchemaCreatorFactory,
                            npocoMappers)
                    : new MainDomSemaphoreLock(loggerFactory.CreateLogger<MainDomSemaphoreLock>(), hostingEnvironment);
            });

            return builder;
        }


        private static IUmbracoBuilder AddPreValueMigrators(this IUmbracoBuilder builder)
        {
            builder.WithCollectionBuilder<PreValueMigratorCollectionBuilder>()
                .Append<RenamingPreValueMigrator>()
                .Append<RichTextPreValueMigrator>()
                .Append<UmbracoSliderPreValueMigrator>()
                .Append<MediaPickerPreValueMigrator>()
                .Append<ContentPickerPreValueMigrator>()
                .Append<NestedContentPreValueMigrator>()
                .Append<DecimalPreValueMigrator>()
                .Append<ListViewPreValueMigrator>()
                .Append<DropDownFlexiblePreValueMigrator>()
                .Append<ValueListPreValueMigrator>()
                .Append<MarkdownEditorPreValueMigrator>();

            return builder;
        }

        public static IUmbracoBuilder AddLogViewer(this IUmbracoBuilder builder)
        {
            builder.Services.AddUnique<ILogViewerConfig, LogViewerConfig>();
            builder.SetLogViewer<SerilogJsonLogViewer>();
            builder.Services.AddUnique<ILogViewer>(factory => new SerilogJsonLogViewer(factory.GetRequiredService<ILogger<SerilogJsonLogViewer>>(),
                factory.GetRequiredService<ILogViewerConfig>(),
                factory.GetRequiredService<ILoggingConfiguration>(),
                Log.Logger));

            return builder;
        }


        public static IUmbracoBuilder AddCoreNotifications(this IUmbracoBuilder builder)
        {
            // add handlers for sending user notifications (i.e. emails)
            builder.Services.AddUnique<UserNotificationsHandler.Notifier>();
            builder
                .AddNotificationHandler<ContentSavedNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentSortedNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentPublishedNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentMovedNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentMovedToRecycleBinNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentCopiedNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentRolledBackNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentSentToPublishNotification, UserNotificationsHandler>()
                .AddNotificationHandler<ContentUnpublishedNotification, UserNotificationsHandler>()
                .AddNotificationHandler<AssignedUserGroupPermissionsNotification, UserNotificationsHandler>()
                .AddNotificationHandler<PublicAccessEntrySavedNotification, UserNotificationsHandler>();

            // add handlers for building content relations
            builder
                .AddNotificationHandler<ContentCopiedNotification, RelateOnCopyNotificationHandler>()
                .AddNotificationHandler<ContentMovedNotification, RelateOnTrashNotificationHandler>()
                .AddNotificationHandler<ContentMovedToRecycleBinNotification, RelateOnTrashNotificationHandler>()
                .AddNotificationHandler<MediaMovedNotification, RelateOnTrashNotificationHandler>()
                .AddNotificationHandler<MediaMovedToRecycleBinNotification, RelateOnTrashNotificationHandler>();

            // add notification handlers for property editors
            builder
                .AddNotificationHandler<ContentSavingNotification, BlockEditorPropertyHandler>()
                .AddNotificationHandler<ContentCopyingNotification, BlockEditorPropertyHandler>()
                .AddNotificationHandler<ContentSavingNotification, NestedContentPropertyHandler>()
                .AddNotificationHandler<ContentCopyingNotification, NestedContentPropertyHandler>()
                .AddNotificationHandler<ContentCopiedNotification, FileUploadPropertyEditor>()
                .AddNotificationHandler<ContentDeletedNotification, FileUploadPropertyEditor>()
                .AddNotificationHandler<MediaDeletedNotification, FileUploadPropertyEditor>()
                .AddNotificationHandler<MediaSavingNotification, FileUploadPropertyEditor>()
                .AddNotificationHandler<MemberDeletedNotification, FileUploadPropertyEditor>()
                .AddNotificationHandler<ContentCopiedNotification, ImageCropperPropertyEditor>()
                .AddNotificationHandler<ContentDeletedNotification, ImageCropperPropertyEditor>()
                .AddNotificationHandler<MediaDeletedNotification, ImageCropperPropertyEditor>()
                .AddNotificationHandler<MediaSavingNotification, ImageCropperPropertyEditor>()
                .AddNotificationHandler<MemberDeletedNotification, ImageCropperPropertyEditor>();

            // add notification handlers for redirect tracking
            builder
                .AddNotificationHandler<ContentPublishingNotification, RedirectTrackingHandler>()
                .AddNotificationHandler<ContentPublishedNotification, RedirectTrackingHandler>()
                .AddNotificationHandler<ContentMovingNotification, RedirectTrackingHandler>()
                .AddNotificationHandler<ContentMovedNotification, RedirectTrackingHandler>();

            // Add notification handlers for DistributedCache
            builder
                .AddNotificationHandler<DictionaryItemDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<DictionaryItemSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<LanguageSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<LanguageDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MemberSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MemberDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<PublicAccessEntrySavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<PublicAccessEntryDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<UserSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<UserDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<UserGroupWithUsersSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<UserGroupDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MemberGroupDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MemberGroupSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<DataTypeDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<DataTypeSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<TemplateDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<TemplateSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<RelationTypeDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<RelationTypeSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<DomainDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<DomainSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MacroSavedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MacroDeletedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MediaTreeChangeNotification, DistributedCacheBinder>()
                .AddNotificationHandler<ContentTypeChangedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MediaTypeChangedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<MemberTypeChangedNotification, DistributedCacheBinder>()
                .AddNotificationHandler<ContentTreeChangeNotification, DistributedCacheBinder>()
                ;
            // add notification handlers for auditing
            builder
                .AddNotificationHandler<MemberSavedNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<MemberDeletedNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<AssignedMemberRolesNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<RemovedMemberRolesNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<ExportedMemberNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<UserSavedNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<UserDeletedNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<UserGroupWithUsersSavedNotification, AuditNotificationsHandler>()
                .AddNotificationHandler<AssignedUserGroupPermissionsNotification, AuditNotificationsHandler>();

            return builder;
        }
    }
}
