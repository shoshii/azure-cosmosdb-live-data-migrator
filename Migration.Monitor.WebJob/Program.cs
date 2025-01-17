﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Cosmos;
using Migration.Shared;
using Migration.Shared.DataContracts;

namespace Migration.Monitor.WebJob
{
    internal class Program
    {
        public const string SourceName = "MigrationMonitor";
        public const string MigrationClientUserAgentPrefix = "MigrationMonitor.MigrationMetadata";
        public const string SourceClientUserAgentPrefix = "MigrationMonitor.Source";
        public const string DestinationClientUserAgentPrefix = "MigrationMonitor.Destination";

        private const int SleepTime = 10000;
        private const int MaxConcurrentMonitoringJobs = 5;

        private static readonly ConcurrentDictionary<string, CosmosClient> sourceClients =
            new ConcurrentDictionary<string, CosmosClient>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, CosmosClient> destinationClients =
            new ConcurrentDictionary<string, CosmosClient>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, ChangeFeedEstimator> estimators =
            new ConcurrentDictionary<string, ChangeFeedEstimator>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, BlobContainerClient> deadletterClients =
            new ConcurrentDictionary<string, BlobContainerClient>(StringComparer.OrdinalIgnoreCase);

#pragma warning disable IDE0060 // Remove unused parameter

        private static void Main(string[] args)
#pragma warning restore IDE0060 // Remove unused parameter
        {
            try
            {
                EnvironmentConfig.Initialize();

                TelemetryConfiguration telemetryConfig = new TelemetryConfiguration(
                    EnvironmentConfig.Singleton.AppInsightsInstrumentationKey);
                TelemetryHelper.Initilize(telemetryConfig, SourceName);
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    "UNHANDLED EXCEPTION during initialization before TelemetryClient oculd be created: {0}",
                    error);

                throw;
            }

            try
            {
                KeyVaultHelper.Initialize(new DefaultAzureCredential());

                RunAsync().Wait();
            }
            catch (Exception unhandledException)
            {
                TelemetryHelper.Singleton.LogError(
                    "UNHANDLED EXCEPTION: {0}",
                    unhandledException);

                throw;
            }
        }

        private static async Task RunAsync()
        {
            await Task.Yield();

            using (CosmosClient client =
               KeyVaultHelper.Singleton.CreateCosmosClientFromKeyVault(
                   EnvironmentConfig.Singleton.MigrationMetadataCosmosAccountName,
                   MigrationClientUserAgentPrefix,
                   useBulk: false,
                   retryOn429Forever: true))
            {
                Database db = await client
                    .CreateDatabaseIfNotExistsAsync(EnvironmentConfig.Singleton.MigrationMetadataDatabaseName)
                    .ConfigureAwait(false);

                Container container = await db
                    .CreateContainerIfNotExistsAsync(
                        new ContainerProperties(EnvironmentConfig.Singleton.MigrationMetadataContainerName, "/id"))
                    .ConfigureAwait(false);

                Container leaseContainer = await db
                    .CreateContainerIfNotExistsAsync(
                        new ContainerProperties(EnvironmentConfig.Singleton.MigrationLeasesContainerName, "/id"))
                    .ConfigureAwait(false);


                while (true)
                {
                    try
                    {

                        List<MigrationConfig> configDocs = new List<MigrationConfig>();
                        FeedIterator<MigrationConfig> iterator = container.GetItemQueryIterator<MigrationConfig>(
                            "select * from c where NOT c.completed");

                        while (iterator.HasMoreResults)
                        {
                            FeedResponse<MigrationConfig> response = await iterator.ReadNextAsync().ConfigureAwait(false);
                            configDocs.AddRange(response.Resource);
                        }

                        if (configDocs.Count == 0)
                        {
                            TelemetryHelper.Singleton.LogInfo(
                                "No Migration to monitor for process '{0}'",
                                Process.GetCurrentProcess().Id);
                        }
                        else
                        {
                            TelemetryHelper.Singleton.LogInfo(
                                "Starting to monitor migration by process '{0}' for {1} active migrations",
                                Process.GetCurrentProcess().Id,
                                configDocs.Count);

                            for (int i = 0; i < configDocs.Count; i++)
                            {
                                TelemetryHelper.Singleton.LogInfo(
                                    "Starting to retrieve migration status for migration '{0}/{1}' ({2})",
                                    configDocs[i].DestDbName,
                                    configDocs[i].DestCollectionName,
                                    configDocs[i].Id);

                                if (!estimators.TryGetValue(configDocs[i].ProcessorName, out ChangeFeedEstimator estimator))
                                {
                                    estimator = container.GetChangeFeedEstimator(
                                        configDocs[i].ProcessorName,
                                        leaseContainer);

                                    estimators.TryAdd(configDocs[i].ProcessorName, estimator);
                                }

                                if (!deadletterClients.TryGetValue(
                                    configDocs[i].ProcessorName,
                                    out BlobContainerClient deadletterClient))
                                {
                                    deadletterClient = KeyVaultHelper.Singleton.GetBlobContainerClientFromKeyVault(
                                        EnvironmentConfig.Singleton.DeadLetterAccountName,
                                        configDocs[i].Id?.ToLowerInvariant().Replace("-", String.Empty));

                                    deadletterClients.TryAdd(configDocs[i].ProcessorName, deadletterClient);
                                }

                                await TrackMigrationProgressAsync(container, estimator, deadletterClient, configDocs[i])
                                    .ConfigureAwait(false);

                                TelemetryHelper.Singleton.LogInfo(
                                    "Retrieved migration status for migration '{0}/{1}' ({2})",
                                    configDocs[i].DestDbName,
                                    configDocs[i].DestCollectionName,
                                    configDocs[i].Id);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        TelemetryHelper.Singleton.LogWarning("Failed to retrieve migration statistics. Retrying... Exception: {0}",
                            error);
                    }

                    await Task.Delay(SleepTime).ConfigureAwait(false);
                }
            }
        }

        private static async Task<long> GetPoisonMessageCountAsync(BlobContainerClient deadLetterClient)
        {
            TelemetryHelper.Singleton.LogInfo("--> GetPoisonMessageCountAsync {0}", deadLetterClient.Name);
            try
            {
                AsyncPageable<BlobItem> blobsPagable = deadLetterClient.GetBlobsAsync(BlobTraits.All, BlobStates.None);
                int poisonMessageCount = 0;
                await foreach (BlobItem blob in blobsPagable.ConfigureAwait(false))
                {
                    TelemetryHelper.Singleton.LogInfo("... Blob {0}", blob.Name);
                    if (blob.Metadata.TryGetValue(
                        EnvironmentConfig.DeadLetterMetaDataSuccessfulRetryStatusKey,
                        out string successfulRetryStatusRaw) &&
                        long.TryParse(successfulRetryStatusRaw, out long successfulRetryStatus) &&
                        successfulRetryStatus > 0)
                    {
                        // all poison messages in this blob have been successfully retried
                        // safe to ignore
                        TelemetryHelper.Singleton.LogInfo(
                            "All poison messages in this blob have been successfully retried safe to ignore - Successful retries {0} - {1}",
                            successfulRetryStatusRaw,
                            successfulRetryStatus);
                        continue;
                    }

                    TelemetryHelper.Singleton.LogInfo("Before GetBlobClient");
                    BlobClient blobClient = deadLetterClient.GetBlobClient(blob.Name);
                    MemoryStream downloadStream = new MemoryStream();
                    TelemetryHelper.Singleton.LogInfo("Before Download");
                    Response rsp = await blobClient.DownloadToAsync(downloadStream).ConfigureAwait(false);
                    TelemetryHelper.Singleton.LogInfo("After Download {0}", rsp.Status);
                    string blobContent = Encoding.UTF8.GetString(downloadStream.ToArray());

                    int failedDocCount = Regex.Matches(blobContent, EnvironmentConfig.FailedDocSeperator).Count + 1;

                    TelemetryHelper.Singleton.LogInfo(
                        "FailedDocCount: {0}, Blob: '{1}'",
                        failedDocCount,
                        blobContent);

                    if (!blob.Metadata.TryGetValue(
                        EnvironmentConfig.DeadLetterMetaSuccessfulRetryCountKey,
                        out string successfulRetryCountRaw) ||
                        !int.TryParse(successfulRetryCountRaw, out int successfulRetryCount))
                    {
                        successfulRetryCount = 0;
                    }

                    if (successfulRetryCount >= failedDocCount)
                    {
                        blob.Metadata[EnvironmentConfig.DeadLetterMetaDataSuccessfulRetryStatusKey] = "1";
                        Azure.Response<BlobInfo> updateResponse = await blobClient.SetMetadataAsync(
                            blob.Metadata,
                            new BlobRequestConditions
                            {
                                IfMatch = blob.Properties.ETag
                            }).ConfigureAwait(false);

                        TelemetryHelper.Singleton.LogInfo(
                            "Marked SuccessfulRetryStatus for posion message blob '{0}'",
                            blob.Name);
                    }

                    Interlocked.Add(ref poisonMessageCount, Math.Max(0, failedDocCount - successfulRetryCount));
                }

                TelemetryHelper.Singleton.LogInfo("<-- GetPoisonMessageCountAsync {0}", poisonMessageCount);
                return poisonMessageCount;
            }
            catch (Exception error)
            {
                TelemetryHelper.Singleton.LogWarning(
                    "Failed to get number of poison messages. Retrying on next iteration... Exception: {0}",
                    error);

                TelemetryHelper.Singleton.LogInfo("<-- GetPoisonMessageCountAsync {0}", -1);

                return -1;
            }
        }

        private static async Task<long> GetUnprocessedTransactionCountAsync(ChangeFeedEstimator estimator)
        {
            try
            {
                FeedIterator<ChangeFeedProcessorState> iterator = estimator.GetCurrentStateIterator();
                List<ChangeFeedProcessorState> states = new List<ChangeFeedProcessorState>();
                while (iterator.HasMoreResults)
                {
                    FeedResponse<ChangeFeedProcessorState> response = await iterator.ReadNextAsync().ConfigureAwait(false);
                    states.AddRange(response.Resource);
                }

                return states.Sum(s => s.EstimatedLag);
            }
            catch (Exception error)
            {
                TelemetryHelper.Singleton.LogWarning(
                    "Failed to get estimated number of unprocessed documents. Retrying on next iteration... Exception: {0}",
                    error);

                return -1;
            }
        }

        private static Task<long> GetSourceDocumentCountAsync(Container container, MigrationConfig migrationConfig)
        {
            return GetDocumentCountAsync(
                container,
                migrationConfig,
                !String.IsNullOrWhiteSpace(migrationConfig.SourcePartitionKeyValueFilter));
        }

        private static Task<long> GetTargetDocumentCountAsync(
            Container container,
            MigrationConfig migrationConfig)
        {
            return GetDocumentCountAsync(
                container,
                migrationConfig,
                !String.IsNullOrWhiteSpace(migrationConfig.SourcePartitionKeyValueFilter) &&
                String.Equals(migrationConfig.SourcePartitionKeys, migrationConfig.TargetPartitionKey));
        }

        private static async Task<long> GetDocumentCountAsync(
            Container container,
            MigrationConfig migrationConfig,
            bool filterIsPartitionKey)
        {
            if (container == null) { throw new ArgumentNullException(nameof(container)); }
            if (migrationConfig == null) { throw new ArgumentNullException(nameof(migrationConfig)); }

            TelemetryHelper.Singleton.LogInfo(
                            "Retrieving document count of container '{0}/{1}' with filter '{2}'",
                            container.Database.Id,
                            container.Id,
                            migrationConfig.SourcePartitionKeyValueFilter);

            try
            {
                long count = String.IsNullOrWhiteSpace(migrationConfig.SourcePartitionKeyValueFilter)
                    ? await GetDocumentCountEntireContainerAsync(container).ConfigureAwait(false)
                    : await GetDocumentCountWithFilterAsync(
                        container,
                        migrationConfig.SourcePartitionKeys,
                        migrationConfig.SourcePartitionKeyValueFilter,
                        filterIsPartitionKey).ConfigureAwait(false);

                TelemetryHelper.Singleton.LogInfo(
                    "Retrieved document count of container '{0}/{1}' - {2}",
                    container.Database.Id,
                    container.Id,
                    count);

                return count;
            }
            catch(Exception error)
            {
                TelemetryHelper.Singleton.LogInfo(
                   "Faield to retrieve document count of container '{0}/{1}' - Exception: {2}",
                   container.Database.Id,
                   container.Id,
                   error);

                throw;
            }
        }

        private static async Task<long> GetDocumentCountEntireContainerAsync(Container container)
        {
            if (container == null) { throw new ArgumentNullException(nameof(container)); }

            ContainerRequestOptions requestOptions = new ContainerRequestOptions { PopulateQuotaInfo = true };
            ContainerResponse result = await container.ReadContainerAsync(requestOptions);
            string usage = result.Headers["x-ms-resource-usage"];
            string[] quotas = usage.Split(";");
            const string DocumentsCountPrefix = "documentsCount=";

            return long.Parse(
                quotas.Single(q => q.StartsWith(DocumentsCountPrefix))[DocumentsCountPrefix.Length..]);
        }

        private static async Task<long> GetDocumentCountWithFilterAsync(
            Container container,
            String sourcePartitionKeyName,
            String sourcePartitionKeyValue,
            bool filterIsPartitionKey)
        {
            if (container == null) { throw new ArgumentNullException(nameof(container)); }
            if (String.IsNullOrWhiteSpace(sourcePartitionKeyName)) { throw new ArgumentNullException(nameof(sourcePartitionKeyName)); }
            if (sourcePartitionKeyValue == null) { throw new ArgumentNullException(nameof(sourcePartitionKeyValue)); }

            QueryDefinition queryDef = new QueryDefinition(
                String.Format(
                    CultureInfo.InvariantCulture,
                    "SELECT VALUE COUNT(1) FROM c WHERE c.{0} = @pkValue",
                    sourcePartitionKeyName.StartsWith("/") ? 
                            sourcePartitionKeyName[1..sourcePartitionKeyName.Length] : sourcePartitionKeyName))
                .WithParameter("@pkValue", sourcePartitionKeyValue);

            QueryRequestOptions requestOptions = new QueryRequestOptions
            {
                ConsistencyLevel = ConsistencyLevel.Eventual
            };

            if (filterIsPartitionKey)
            {
                requestOptions.PartitionKey = new PartitionKey(sourcePartitionKeyValue);
            }

            long recordCount = 0;
            FeedIterator<long> queryIterator = container.GetItemQueryIterator<long>(queryDef, null, requestOptions);
            while (queryIterator.HasMoreResults)
            {
                FeedResponse<long> pagedResponse = await queryIterator.ReadNextAsync();
                if (pagedResponse.Resource != null)
                {
                    foreach(long current in pagedResponse.Resource)
                    {
                        recordCount += current;
                    }
                }
            }

            return recordCount;
        }

        private static async Task TrackMigrationProgressAsync(
            Container migrationContainer,
            ChangeFeedEstimator estimator,
            BlobContainerClient deadLetterClient,
            MigrationConfig migrationConfig)
        {
            if (migrationContainer == null) { throw new ArgumentNullException(nameof(migrationContainer)); }
            if (estimator == null) { throw new ArgumentNullException(nameof(estimator)); }
            if (migrationConfig == null) { throw new ArgumentNullException(nameof(migrationConfig)); }

            CosmosClient sourceClient = GetOrCreateSourceCosmosClient(migrationConfig.MonitoredAccount);
            CosmosClient destinationClient = GetOrCreateSourceCosmosClient(migrationConfig.DestAccount);
            Container sourceContainer = sourceClient.GetContainer(
                migrationConfig.MonitoredDbName,
                migrationConfig.MonitoredCollectionName);
            Container destinationContainer = destinationClient.GetContainer(
                migrationConfig.DestDbName,
                migrationConfig.DestCollectionName);

            while (true)
            {
                MigrationConfig migrationConfigSnapshot = await migrationContainer
                    .ReadItemAsync<MigrationConfig>(migrationConfig.Id, new PartitionKey(migrationConfig.Id))
                    .ConfigureAwait(false);

                DateTimeOffset now = DateTimeOffset.UtcNow;

                Task<long>[] tasks = new Task<long>[4];
                tasks[0] = GetPoisonMessageCountAsync(deadLetterClient);
                tasks[1] = GetUnprocessedTransactionCountAsync(estimator);
                tasks[2] = GetSourceDocumentCountAsync(sourceContainer, migrationConfig);
                tasks[3] = GetTargetDocumentCountAsync(destinationContainer, migrationConfig);

                long[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

                long poisonMessageCount = results[0];
                long unprocessedTransactionCount = results[1];
                long sourceCollectionCount = results[2];
                long currentDestinationCollectionCount = results[3];
                double currentPercentage = sourceCollectionCount == 0 ?
                    100 :
                    currentDestinationCollectionCount * 100.0 / sourceCollectionCount;
                long insertedCount =
                    currentDestinationCollectionCount - migrationConfigSnapshot.MigratedDocumentCount;

                long lastMigrationActivityRecorded = Math.Max(
                        migrationConfigSnapshot.StatisticsLastMigrationActivityRecordedEpochMs,
                        migrationConfigSnapshot.StartTimeEpochMs);
                long nowEpochMs = now.ToUnixTimeMilliseconds();

                double currentRate = nowEpochMs != lastMigrationActivityRecorded ? insertedCount * 1000.0 / (nowEpochMs - lastMigrationActivityRecorded) : 0;
                    
                long totalSeconds = 
                    (lastMigrationActivityRecorded - migrationConfigSnapshot.StartTimeEpochMs) / 1000;
                double averageRate = totalSeconds > 0 ? currentDestinationCollectionCount / totalSeconds : 0;

                long etaMs = averageRate > 0
                    ? (long)((sourceCollectionCount - currentDestinationCollectionCount) * 1000 / averageRate)
                    : (long)TimeSpan.FromDays(100).TotalMilliseconds;

                migrationConfigSnapshot.ExpectedDurationLeft = etaMs;
                migrationConfigSnapshot.AvgRate = averageRate;
                migrationConfigSnapshot.CurrentRate = currentRate;
                migrationConfigSnapshot.SourceCountSnapshot = sourceCollectionCount;
                migrationConfigSnapshot.DestinationCountSnapshot = currentDestinationCollectionCount;
                migrationConfigSnapshot.PercentageCompleted = currentPercentage;
                migrationConfigSnapshot.StatisticsLastUpdatedEpochMs = nowEpochMs;
                migrationConfigSnapshot.MigratedDocumentCount = currentDestinationCollectionCount;
                migrationConfigSnapshot.UnprocessedTransactionCountSnapshot = unprocessedTransactionCount;
                migrationConfigSnapshot.PoisonMessageCountSnapshot = poisonMessageCount;
                if (insertedCount > 0)
                {
                    migrationConfigSnapshot.StatisticsLastMigrationActivityRecordedEpochMs = nowEpochMs;
                }

                try
                {
                    ItemResponse<MigrationConfig> response = await migrationContainer
                        .ReplaceItemAsync(
                            migrationConfigSnapshot,
                            migrationConfigSnapshot.Id,
                            new PartitionKey(migrationConfigSnapshot.Id),
                            new ItemRequestOptions
                            {
                                IfMatchEtag = migrationConfigSnapshot.ETag
                            })
                        .ConfigureAwait(false);
                }
                catch (CosmosException error)
                {
                    if (error.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
                    {
                        TelemetryHelper.Singleton.LogInfo(
                            "Conflict when trying to update statistics for migration '{0}/{1}' ({2})",
                            migrationConfig.DestDbName,
                            migrationConfig.DestCollectionName,
                            migrationConfig.Id);
                        continue;
                    }

                    throw;
                }

                TelemetryHelper.Singleton.TrackStatistics(
                    sourceCollectionCount,
                    currentDestinationCollectionCount,
                    currentPercentage,
                    currentRate,
                    averageRate,
                    etaMs);

                return;
            }
        }

        private static CosmosClient GetOrCreateSourceCosmosClient(string accountName)
        {
            return GetOrCreateCosmosClient(
                sourceClients,
                SourceClientUserAgentPrefix,
                accountName);
        }

        private static CosmosClient GetOrCreateDestinationCosmosClient(string accountName)
        {
            return GetOrCreateCosmosClient(
                destinationClients,
                DestinationClientUserAgentPrefix,
                accountName);
        }

        private static CosmosClient GetOrCreateCosmosClient(
            ConcurrentDictionary<string, CosmosClient> cache,
            string userAgentPrefix,
            string accountName)
        {
            if (cache == null) { throw new ArgumentNullException(nameof(cache)); }
            if (String.IsNullOrWhiteSpace(accountName)) { throw new ArgumentNullException(nameof(accountName)); }

            lock (cache)
            {
                if (cache.TryGetValue(accountName, out CosmosClient client))
                {
                    return client;
                }

                client = KeyVaultHelper.Singleton.CreateCosmosClientFromKeyVault(
                    accountName,
                    userAgentPrefix,
                    useBulk: false,
                    retryOn429Forever: true);
                cache.TryAdd(accountName, client);

                return client;
            }
        }
    }
}