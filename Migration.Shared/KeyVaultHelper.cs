﻿using System;
using System.Globalization;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace Migration.Shared
{
    public class KeyVaultHelper
    {
        private const string CosmosConnectionStringSecretNameSuffix = "-CosmosDB-ConnectionString";
        private const string BlobStorageConnectionStringSecretNameSuffix = "-BlobStorage-ConnectionString";

        private static KeyVaultHelper singletonInstance;

        private readonly SecretClient keyVaultClient;
        private readonly string keyVaultUri;

        private KeyVaultHelper(SecretClient keyVaultClient, string keyVaultUri)
        {
            this.keyVaultClient = keyVaultClient;
            this.keyVaultUri = keyVaultUri;
        }

        public static KeyVaultHelper Singleton
        {
            get
            {
                if (singletonInstance == null)
                {
                    throw new InvalidOperationException("KeyVaultHelper has not yet been initialized.");
                }

                return singletonInstance;
            }
        }

        public static void Initialize(TokenCredential credential)
        {
            if (credential == null) { throw new ArgumentNullException(nameof(credential)); }

            for (int i = 0; i < 10; i++)
            {
                Uri keyVaultUri = new Uri(EnvironmentConfig.Singleton.KeyVaultUri);
                try
                {
                    singletonInstance = new KeyVaultHelper(
                        new SecretClient(keyVaultUri, credential),
                        keyVaultUri.ToString());

                    return;
                }
                catch (Exception error)
                {
                    string msg = String.Format(
                                CultureInfo.InvariantCulture,
                                "Can't initialize key value '{0}', Error: {1}",
                                keyVaultUri,
                                error);

                    Console.WriteLine(msg);

                    if (i == 9)
                    {
                        throw new InvalidOperationException(msg);
                    }
                }

                Task.Delay(1000).Wait();
            }
        }

        private string GetSecret(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentNullException(nameof(name)); }

            try
            {
                return this.keyVaultClient.GetSecret(name).Value.Value;
            }
            catch (Exception error)
            {
                TelemetryHelper.Singleton.LogError(
                    "Cannot retrieve secret '{0}' from key vault '{1}'. Exception: {2}",
                    name,
                    this.keyVaultUri,
                    error);

                throw;
            }
        }

        public CosmosClient CreateCosmosClientFromKeyVault(
            string accountName,
            string userAgentPrefix,
            bool useBulk,
            bool retryOn429Forever)
        {
            if (String.IsNullOrWhiteSpace(accountName)) { throw new ArgumentNullException(nameof(accountName)); }

            CosmosClientOptions options = new CosmosClientOptions
            {
                AllowBulkExecution = useBulk,
            };

            if (retryOn429Forever)
            {
                options.MaxRetryAttemptsOnRateLimitedRequests = Int32.MaxValue;
                options.MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(Int32.MaxValue / 1000);
            }

            if (!String.IsNullOrWhiteSpace(userAgentPrefix))
            {
                options.ApplicationName = userAgentPrefix;
            }

            string connectionString = KeyVaultHelper.Singleton.GetSecret(accountName + CosmosConnectionStringSecretNameSuffix);

            return new CosmosClient(connectionString, options);
        }

        public BlobContainerClient GetBlobContainerClientFromKeyVault(
            string accountName,
            string containerName)
        {
            if (String.IsNullOrWhiteSpace(accountName)) { throw new ArgumentNullException(nameof(accountName)); }
            if (String.IsNullOrWhiteSpace(containerName)) { throw new ArgumentNullException(nameof(containerName)); }

            string connectionString = KeyVaultHelper.Singleton.GetSecret(accountName + BlobStorageConnectionStringSecretNameSuffix);

            BlobServiceClient serviceClient = new BlobServiceClient(connectionString);
            return serviceClient.GetBlobContainerClient(containerName);
        }
    }
}