﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using AuthJanitor.UI.Shared.Models;
using AuthJanitor.EventSinks;
using AuthJanitor.IdentityServices;
using AuthJanitor.Integrations.DataStores;
using AuthJanitor.Providers;
using AuthJanitor.SecureStorage;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace AuthJanitor.UI.Shared.MetaServices
{
    public class TaskExecutionMetaService
    {
        private readonly IDataStore<ManagedSecret> _managedSecrets;
        private readonly IDataStore<RekeyingTask> _rekeyingTasks;
        private readonly IDataStore<Resource> _resources;
        private readonly ISecureStorage _secureStorageProvider;

        private readonly ProviderManagerService _providerManagerService;

        private readonly EventDispatcherMetaService _eventDispatcherMetaService;
        private readonly IIdentityService _identityService;

        public TaskExecutionMetaService(
            EventDispatcherMetaService eventDispatcherMetaService,
            IIdentityService identityService,
            ProviderManagerService providerManagerService,
            IDataStore<ManagedSecret> managedSecrets,
            IDataStore<RekeyingTask> rekeyingTasks,
            IDataStore<Resource> resources,
            ISecureStorage secureStorageProvider)
        {
            _eventDispatcherMetaService = eventDispatcherMetaService;
            _identityService = identityService;
            _providerManagerService = providerManagerService;
            _managedSecrets = managedSecrets;
            _rekeyingTasks = rekeyingTasks;
            _resources = resources;
            _secureStorageProvider = secureStorageProvider;
        }

        public async Task CacheBackCredentialsForTaskIdAsync(Guid taskId, CancellationToken cancellationToken)
        {
            var task = await _rekeyingTasks.GetOne(taskId, cancellationToken);
            if (task == null)
                throw new KeyNotFoundException("Task not found");

            if (task.ConfirmationType != TaskConfirmationStrategies.AdminCachesSignOff)
                throw new InvalidOperationException("Task does not persist credentials");

            if (_secureStorageProvider == null)
                throw new NotSupportedException("Must register an ISecureStorageProvider");

            var credentialId = await _identityService.GetAccessTokenOnBehalfOfCurrentUserAsync()
                                       .ContinueWith(t => _secureStorageProvider.Persist(task.Expiry, t.Result))
                                       .Unwrap();

            task.PersistedCredentialId = credentialId;
            task.PersistedCredentialUser = _identityService.UserName;

            await _rekeyingTasks.Update(task, cancellationToken);
        }

        public async Task ExecuteTask(Guid taskId, CancellationToken cancellationToken)
        {
            // Prepare record
            var task = await _rekeyingTasks.GetOne(taskId, cancellationToken);
            task.RekeyingInProgress = true;
            var rekeyingAttemptLog = new RekeyingAttemptLogger();
            task.Attempts.Add(rekeyingAttemptLog);
            await _rekeyingTasks.Update(task, cancellationToken);

            var logUpdateCancellationTokenSource = new CancellationTokenSource();
            var logUpdateTask = Task.Run(async () =>
            {
                while (task.RekeyingInProgress)
                {
                    await Task.Delay(15 * 1000);
                    await _rekeyingTasks.Update(task, cancellationToken);
                }
            }, logUpdateCancellationTokenSource.Token);

            // Retrieve credentials for Task
            AccessTokenCredential credential = null;
            try
            {
                if (task.ConfirmationType == TaskConfirmationStrategies.AdminCachesSignOff)
                {
                    if (task.PersistedCredentialId == default)
                        throw new KeyNotFoundException("Cached sign-off is preferred but no credentials were persisted!");

                    if (_secureStorageProvider == null)
                        throw new NotSupportedException("Must register an ISecureStorageProvider");

                    credential = await _secureStorageProvider.Retrieve<AccessTokenCredential>(task.PersistedCredentialId);
                }
                else if (task.ConfirmationType == TaskConfirmationStrategies.AdminSignsOffJustInTime)
                    credential = await _identityService.GetAccessTokenOnBehalfOfCurrentUserAsync();
                else if (task.ConfirmationType.UsesServicePrincipal())
                    credential = await _identityService.GetAccessTokenForApplicationAsync();
                else
                    throw new NotSupportedException("No Access Tokens could be generated for this Task!");

                if (credential == null || string.IsNullOrEmpty(credential.AccessToken))
                    throw new InvalidOperationException("Access Token was found, but was blank or invalid");
            }
            catch (Exception ex)
            {
                await EmbedException(task, ex, cancellationToken, "Exception retrieving Access Token");
                await _eventDispatcherMetaService.DispatchEvent(AuthJanitorSystemEvents.RotationTaskAttemptFailed, nameof(TaskExecutionMetaService.ExecuteTask), task);
                return;
            }

            // Embed credential context in attempt log
            rekeyingAttemptLog.UserDisplayName = credential.Username;
            rekeyingAttemptLog.UserEmail = credential.Username;
            if (task.ConfirmationType.UsesOBOTokens())
            {
                if (!string.IsNullOrEmpty(task.PersistedCredentialUser))
                    rekeyingAttemptLog.UserDisplayName = task.PersistedCredentialUser;
                else
                {
                    rekeyingAttemptLog.UserDisplayName = _identityService.UserName;
                    rekeyingAttemptLog.UserEmail = _identityService.UserEmail;
                }
            }

            // Retrieve targets
            var secret = await _managedSecrets.GetOne(task.ManagedSecretId, cancellationToken);
            rekeyingAttemptLog.LogInformation("Beginning rekeying of secret ID {SecretId}", task.ManagedSecretId);
            var resources = await _resources.Get(r => secret.ResourceIds.Contains(r.ObjectId), cancellationToken);

            await _rekeyingTasks.Update(task, cancellationToken);

            // Execute rekeying workflow
            try
            {
                var providers = resources.Select(r => _providerManagerService.GetProviderInstance(
                    r.ProviderType,
                    r.ProviderConfiguration)).ToList();

                // Link in automation bindings from the outer flow
                providers.ForEach(p => p.Credential = credential);

                await _providerManagerService.ExecuteRekeyingWorkflow(rekeyingAttemptLog, secret.ValidPeriod, providers);
            }
            catch (Exception ex)
            {
                rekeyingAttemptLog.IsComplete = true;
                await EmbedException(task, ex, cancellationToken, "Error executing rekeying workflow!");
                await _eventDispatcherMetaService.DispatchEvent(AuthJanitorSystemEvents.RotationTaskAttemptFailed, nameof(TaskExecutionMetaService.ExecuteTask), task);
            }

            // Update Task record
            task.RekeyingInProgress = false;
            task.RekeyingCompleted = rekeyingAttemptLog.IsSuccessfulAttempt;
            task.RekeyingFailed = !rekeyingAttemptLog.IsSuccessfulAttempt;

            logUpdateCancellationTokenSource.Cancel();

            await _rekeyingTasks.Update(task, cancellationToken);

            // Run cleanup if Task is complete
            if (task.RekeyingCompleted)
            {
                try
                {
                    secret.LastChanged = DateTimeOffset.UtcNow;
                    await _managedSecrets.Update(secret, cancellationToken);

                    if (task.PersistedCredentialId != default && task.PersistedCredentialId != Guid.Empty)
                    {
                        rekeyingAttemptLog.LogInformation("Destroying persisted credential");
                        await _secureStorageProvider.Destroy(task.PersistedCredentialId);

                        task.PersistedCredentialId = default;
                        task.PersistedCredentialUser = default;
                    }

                    rekeyingAttemptLog.LogInformation("Completed rekeying workflow for ManagedSecret '{ManagedSecretName}' (ID {ManagedSecretId})", secret.Name, secret.ObjectId);
                    rekeyingAttemptLog.LogInformation("Rekeying task completed");

                    await _rekeyingTasks.Update(task, cancellationToken);
                }
                catch (Exception ex)
                {
                    await EmbedException(task, ex, cancellationToken, "Error cleaning up after rekeying!");
                }


                if (task.ConfirmationType.UsesOBOTokens())
                    await _eventDispatcherMetaService.DispatchEvent(AuthJanitorSystemEvents.RotationTaskCompletedManually, nameof(TaskExecutionMetaService.ExecuteTask), task);
                else
                    await _eventDispatcherMetaService.DispatchEvent(AuthJanitorSystemEvents.RotationTaskCompletedAutomatically, nameof(TaskExecutionMetaService.ExecuteTask), task);
            }
            else
                await _eventDispatcherMetaService.DispatchEvent(AuthJanitorSystemEvents.RotationTaskAttemptFailed, nameof(TaskExecutionMetaService.ExecuteTask), task);
        }

        private async Task EmbedException(RekeyingTask task, Exception ex, CancellationToken cancellationToken, string text = "Exception Occurred")
        {
            var myAttempt = task.Attempts.OrderByDescending(a => a.AttemptStarted).First();
            if (text != default) myAttempt.LogCritical(ex, text);
            myAttempt.OuterException = Regex.Replace(JsonConvert.SerializeObject(ex, Formatting.Indented), "Bearer [A-Za-z0-9\\-\\._~\\+\\/]+=*", "<<REDACTED BEARER TOKEN>>");
            //myAttempt.OuterException = $"{ex.Message}{Environment.NewLine}{ex.StackTrace}";
            task.RekeyingInProgress = false;
            task.RekeyingFailed = true;
            await _rekeyingTasks.Update(task, cancellationToken);
        }
    }
}
