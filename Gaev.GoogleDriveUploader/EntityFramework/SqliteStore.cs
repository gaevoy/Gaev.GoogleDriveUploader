using System;
using System.Threading.Tasks;
using Gaev.GoogleDriveUploader.Domain;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using Microsoft.EntityFrameworkCore;

namespace Gaev.GoogleDriveUploader.EntityFramework
{
    public class SqliteStore : IDataStore
    {
        public async Task StoreAsync<T>(string key, T value)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key MUST have a value");
            }

            var serialized = NewtonsoftJsonSerializer.Instance.Serialize(value);
            key = GenerateStoredKey(key, typeof(T));

            using (var db = new DbSession())
            {
                var item = await db.Store.FindAsync(key);
                if (item == null)
                {
                    item = new KeyValueStore {Key = key};
                    db.Store.Add(item);
                }

                item.Value = serialized;
                await db.SaveChangesAsync();
            }
        }

        public async Task DeleteAsync<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key MUST have a value");
            }

            key = GenerateStoredKey(key, typeof(T));

            using (var db = new DbSession())
            {
                var item = await db.Store.FindAsync(key);
                if (item != null)
                {
                    db.Store.Remove(item);
                    await db.SaveChangesAsync();
                }
            }
        }

        public async Task<T> GetAsync<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key MUST have a value");
            }

            key = GenerateStoredKey(key, typeof(T));
            using (var db = new DbSession())
            {
                var item = await db.Store.FindAsync(key);
                if (item == null)
                    return default(T);
                return NewtonsoftJsonSerializer.Instance.Deserialize<T>(item.Value);
            }
        }

        public async Task ClearAsync()
        {
            using (var db = new DbSession())
            {
                db.Store.RemoveRange(await db.Store.ToListAsync());
                await db.SaveChangesAsync();
            }
        }

        public static string GenerateStoredKey(string key, Type t)
        {
            return string.Format("{0}-{1}", t.FullName, key);
        }
    }
}