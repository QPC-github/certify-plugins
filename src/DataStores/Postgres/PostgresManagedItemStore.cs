﻿using Certify.Models;
using Certify.Models.Providers;
using Certify.Providers;
using Newtonsoft.Json;
using Npgsql;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Certify.Datastore.Postgres
{
    public class PostgresManagedItemStore : IManagedItemStore, IDisposable
    {
        private readonly ILog _log;
        private readonly string _connectionString;
        private readonly AsyncRetryPolicy _retryPolicy = Policy.Handle<ArgumentException>()
            .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(1), onRetry: (exception, retryCount, context) =>
            {
                System.Diagnostics.Debug.WriteLine($"Retrying..{retryCount} {exception}");
            });

        public PostgresManagedItemStore(string connectionString = null, ILog log = null)
        {

            _log = log;
            _connectionString = connectionString;
        }

        public async Task Delete(ManagedCertificate item)
        {
            _log?.Warning("Deleting managed item", item);

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                using (var tran = conn.BeginTransaction())
                {
                    using (var cmd = new NpgsqlCommand("DELETE FROM manageditem WHERE id=@id", conn))
                    {
                        cmd.Parameters.Add(new NpgsqlParameter("@id", item.Id));
                        await cmd.ExecuteNonQueryAsync();

                        tran.Commit();
                    }
                }
                await conn.CloseAsync();

            }
        }

        public async Task DeleteAll()
        {
            _log?.Warning("Deleting all managed items");

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                using (var cmd = new NpgsqlCommand("DELETE FROM manageditem", conn))
                {
                    await cmd.ExecuteNonQueryAsync();
                }

                await conn.CloseAsync();
            }
        }

        public async Task DeleteByName(string nameStartsWith)
        {
            using (var db = new NpgsqlConnection(_connectionString))
            {
                await db.OpenAsync();
                using (var tran = db.BeginTransaction())
                {
                    using (var cmd = new NpgsqlCommand("DELETE FROM manageditem WHERE config ->>'Name' LIKE @nameStartsWith || '%' ", db))
                    {
                        cmd.Parameters.Add(new NpgsqlParameter("@nameStartsWith", nameStartsWith));
                        await cmd.ExecuteNonQueryAsync();
                    }

                    tran.Commit();
                }
            }
        }

        public void Dispose()
        {

        }

        public async Task<List<ManagedCertificate>> Find(ManagedCertificateFilter filter)
        {
            var managedCertificates = new List<ManagedCertificate>();

            var sql = @"SELECT * from 
                         (SELECT subquery.id, subquery.config, 
                                                        subquery.config ->> 'Name' as Name, 
                                                        (subquery.config ->> 'DateLastOcspCheck')::timestamp with time zone  as dateLastOcspCheck,
                                                        (subquery.config ->> 'DateLastRenewalInfoCheck')::timestamp with time zone as dateLastRenewalInfoCheck 
                                                   FROM manageditem subquery  
                          ) AS i ";

            var queryParameters = new List<NpgsqlParameter>();
            var conditions = new List<string>();

            if (!string.IsNullOrEmpty(filter.Id))
            {
                conditions.Add(" i.id = @id");
                queryParameters.Add(new NpgsqlParameter("@id", filter.Id));
            }

            if (!string.IsNullOrEmpty(filter.Name))
            {
                conditions.Add(" Name LIKE @name"); // case insensitive string match
                queryParameters.Add(new NpgsqlParameter("@name", filter.Name));
            }

            if (!string.IsNullOrEmpty(filter.Keyword))
            {
                conditions.Add(" (Name LIKE '%' || @keyword || '%')"); // case insensitive string contains
                queryParameters.Add(new NpgsqlParameter("@keyword", filter.Keyword));
            }

            if (filter.LastOCSPCheckMins != null)
            {
                conditions.Add(" dateLastOcspCheck < @ocspCheckDate");
                queryParameters.Add(new NpgsqlParameter("@ocspCheckDate", DateTime.Now.AddMinutes((int)-filter.LastOCSPCheckMins).ToUniversalTime()));
            }

            if (filter.LastRenewalInfoCheckMins != null)
            {
                conditions.Add(" dateLastRenewalInfoCheck < @renewalInfoCheckDate");
                queryParameters.Add(new NpgsqlParameter("@renewalInfoCheckDate", DateTime.Now.AddMinutes((int)-filter.LastRenewalInfoCheckMins).ToUniversalTime()));
            }

            if (filter.ChallengeType != null)
            {
                conditions.Add(" EXISTS (SELECT 1 FROM jsonb_array_elements(i.config -> 'RequestConfig' -> 'Challenges') challenges WHERE challenges.value->>'ChallengeType'=@challengeType)");
                queryParameters.Add(new NpgsqlParameter("@challengeType", filter.ChallengeType));
            }

            if (filter.ChallengeProvider != null)
            {
                conditions.Add(" EXISTS (SELECT 1 FROM jsonb_array_elements(i.config -> 'RequestConfig' -> 'Challenges') challenges WHERE challenges.value->>'ChallengeProvider'=@challengeProvider)");
                queryParameters.Add(new NpgsqlParameter("@challengeProvider", filter.ChallengeProvider));
            }

            if (filter.StoredCredentialKey != null)
            {
                conditions.Add(" EXISTS (SELECT 1 FROM jsonb_array_elements(i.config -> 'RequestConfig' -> 'Challenges') challenges WHERE challenges.value->>'ChallengeCredentialKey'=@challengeCredentialKey)");
                queryParameters.Add(new NpgsqlParameter("@challengeCredentialKey", filter.StoredCredentialKey));
            }

            if (conditions.Any())
            {
                sql += " WHERE ";
                bool isFirstCondition = true;
                foreach (var c in conditions)
                {
                    sql += (!isFirstCondition ? " AND " + c : c);

                    isFirstCondition = false;
                }
            }

            sql += $" ORDER BY Name ASC";

            if (filter?.PageIndex != null && filter?.PageSize != null)
            {
                sql += $" LIMIT {filter.PageSize} OFFSET {filter.PageIndex * filter.PageSize}";
            }
            else if (filter?.MaxResults > 0)
            {
                sql += $" LIMIT {filter.MaxResults}";
            }

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    cmd.Parameters.AddRange(queryParameters.ToArray());

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var itemId = (string)reader["id"];

                            var managedCertificate = JsonConvert.DeserializeObject<ManagedCertificate>((string)reader["config"]);

                            // in some cases users may have previously manipulated the id, causing
                            // duplicates. Correct the ID here (database Id is unique):
                            if (managedCertificate.Id != itemId)
                            {
                                managedCertificate.Id = itemId;
                                _log?.Debug("Postgres: Corrected managed item id: " + managedCertificate.Name);
                            }

                            managedCertificates.Add(managedCertificate);
                        }
                    }
                }
                await conn.CloseAsync();
            }

            return managedCertificates;
        }

        public async Task<ManagedCertificate> GetById(string itemId)
        {
            ManagedCertificate managedCertificate = null;

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                using (var cmd = new NpgsqlCommand("SELECT config FROM manageditem WHERE id=@id", conn))
                {
                    cmd.Parameters.Add(new NpgsqlParameter("@id", itemId));

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            managedCertificate = JsonConvert.DeserializeObject<ManagedCertificate>((string)reader["config"]);
                            managedCertificate.IsChanged = false;
                        }

                        await reader.CloseAsync();
                    }
                }
                await conn.CloseAsync();

            }

            return managedCertificate;
        }

        public async Task<bool> IsInitialised()
        {
            var sql = @"SELECT * from manageditem LIMIT 1;";
            bool queryOK = false;
            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    try
                    {
                        await cmd.ExecuteReaderAsync();
                        queryOK = true;
                    }
                    catch
                    {

                    }
                }
                await conn.CloseAsync();
            }
            return await Task.FromResult(queryOK);
        }

        public Task PerformMaintenance()
        {
            _log?.Warning("Postgres: Maintenance not implemented");
            return Task.CompletedTask;
        }

        public async Task StoreAll(IEnumerable<ManagedCertificate> list)
        {
           foreach(var item in list)
            {
                await Update(item);
            }
        }

        public async Task<ManagedCertificate> Update(ManagedCertificate managedCertificate)
        {
            if (managedCertificate == null)
            {
                return null;
            }

            if (managedCertificate.Id == null)
            {
                managedCertificate.Id = Guid.NewGuid().ToString();
            }

            managedCertificate.Version++;

            if (managedCertificate.Version == long.MaxValue)
            {
                // rollover version, unlikely but accomodate it anyway
                managedCertificate.Version = -1;
            }

            //await _retryPolicy.ExecuteAsync(async () =>
            {
                using (var conn = new NpgsqlConnection(_connectionString))
                {
                    await conn.OpenAsync();

                    ManagedCertificate current = null;

                    // get current version from DB
                    using (var tran = conn.BeginTransaction())
                    {
                        using (var cmd = new NpgsqlCommand("SELECT config FROM manageditem WHERE id=@id", conn))
                        {
                            cmd.Parameters.Add(new NpgsqlParameter("@id", managedCertificate.Id));

                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    current = JsonConvert.DeserializeObject<ManagedCertificate>((string)reader["config"]);
                                    current.IsChanged = false;
                                }

                                await reader.CloseAsync();
                            }
                        }

                        if (current != null)
                        {
                            if (managedCertificate.Version != -1 && current.Version >= managedCertificate.Version)
                            {
                                // version conflict
                                _log?.Error("Managed certificate DB version conflict - newer managed certificate version already stored.");
                            }

                            try
                            {
                                using (var cmd = new NpgsqlCommand("UPDATE manageditem SET config = CAST(@config as jsonb) WHERE id=@id;", conn))
                                {
                                    cmd.Parameters.Add(new NpgsqlParameter("@id", managedCertificate.Id));
                                    cmd.Parameters.Add(new NpgsqlParameter("@config", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonConvert.SerializeObject(managedCertificate, new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore }) });

                                    await cmd.ExecuteNonQueryAsync();
                                }

                                tran.Commit();
                            }
                            catch (NpgsqlException exp)
                            {
                                await tran.RollbackAsync();
                                _log?.Error(exp.ToString());
                                throw;
                            }
                        }
                        else
                        {
                            try
                            {
                                using (var cmd = new NpgsqlCommand("INSERT INTO manageditem(id,config) VALUES(@id,@config);", conn))
                                {
                                    cmd.Parameters.Add(new NpgsqlParameter("@id", managedCertificate.Id));
                                    cmd.Parameters.Add(new NpgsqlParameter("@config", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = JsonConvert.SerializeObject(managedCertificate, new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore }) });

                                    await cmd.ExecuteNonQueryAsync();
                                }

                                tran.Commit();
                            }
                            catch (NpgsqlException exp)
                            {
                                await tran.RollbackAsync();
                                _log?.Error(exp.ToString());
                                throw;
                            }
                        }
                    }

                    await conn.CloseAsync();
                }
            }
            //);

            return managedCertificate;
        }
    }
}
