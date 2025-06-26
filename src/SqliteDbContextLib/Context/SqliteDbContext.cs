using AutoPopulate;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using SQLite;
using SqliteDbContext.Extensions;
using SqliteDbContext.Generator;
using SqliteDbContext.Interfaces;
using SqliteDbContext.Strategies;
using System.Data.Common;
using System.Runtime.CompilerServices;
using static System.Formats.Asn1.AsnWriter;

namespace SqliteDbContext.Context
{
    /// <summary>
    /// A wrapper class that encapsulates a DbContext (of type T) to simulate an in-memory DbContext.
    /// Developers work through this wrapper to generate entities and maintain referential integrity.
    /// </summary>
    public class SqliteDbContext<T> where T : DbContext
    {
        public T Context { get; private set; }
        public IDependencyResolver DependencyResolver { get; private set; }
        public IEntityGenerator EntityGenerator { get; private set; }
        public IKeySeeder KeySeeder { get; private set; }
        public BogusGenerator BogusGenerator { get; private set; }
        public DbContextOptions<T> Options { get; private set; }
        private SqliteConnection _connection;

        public SqliteDbContext(string? DbInstanceName = null, SqliteConnection? conn = null)
        {
            _connection = CreateConnection(DbInstanceName, conn);
            DependencyResolver = new DependencyResolver(Context);
            EntityGenerator = new FakeEntityGenerator();
            EntityGenerator.RecursiveLimit = 0; //limit the number of recursive generations. If set higher than 1, then could generate an invalid set of keys
            EntityGenerator.CollectionLimit = 0;
            EntityGenerator.RandomizationBehavior = AutoPopulate.EntityGenerator.RandomizationType.Fixed;
            KeySeeder = new KeySeeder(Context, DependencyResolver, EntityGenerator);
            KeySeeder.ExistingReferenceChance = 0.7; //0.7 chance of using an existing key vs generating a new instance
            BogusGenerator = new BogusGenerator(DependencyResolver, KeySeeder, EntityGenerator);
        }

        private DbSet<TEntity> Set<TEntity>() where TEntity : class => Context.Set<TEntity>();

        private SqliteConnection CreateConnection(string? dbIntanceName, SqliteConnection? conn)
        {
            dbIntanceName = dbIntanceName ?? Guid.NewGuid().ToString();
            if (conn == null)
            {
                var config = new SqliteConnectionStringBuilder { DataSource = $"{dbIntanceName}:memory:", Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared };
                conn = new SqliteConnection(config.ToString());
            }

            if (conn.State != System.Data.ConnectionState.Open)
            {
                conn.Open();
            }

            Options = new DbContextOptionsBuilder<T>()
                .UseSqlite(conn)
                .Options;

            Context = (T?)Activator.CreateInstance(typeof(T), Options);
            Context?.Database.EnsureDeleted();
            Context?.Database.EnsureCreated();
            return conn;
        }

        /// <summary>
        /// Generates a specified quantity of fake entities of type TEntity.
        /// An optional initialization action allows further customization.
        /// </summary>
        public IEnumerable<TEntity> GenerateEntities<TEntity>(int quantity, Action<TEntity> initAction = null) where TEntity : class, new()
        {
            var entities = new List<TEntity>();
            for (int i = 0; i < quantity; i++)
            {
                var entity = GenerateEntity<TEntity>(initAction);
                entities.Add(entity);
            }
            return entities;
        }

        public TEntity GenerateEntity<TEntity>(Action<TEntity> initAction = null) where TEntity : class, new()
        {
            // Generate a fake entity and remove all navigation properties (initial cleanup).
            var entity = BogusGenerator.GenerateFake<TEntity>();
            entity = BogusGenerator.RemoveNavigationProperties(entity);

            // Clear key properties and assign keys (with recursion depth 0).
            KeySeeder.ClearKeyProperties(entity, 0);
            initAction?.Invoke(entity);
            var (allPrimaryKeysSet, allForeignKeysSet) = AreAllKeysSet(entity);
            KeySeeder.AssignKeys(entity, 0, allPrimaryKeysSet, allForeignKeysSet);

            // Attach the entity to the context.
            Set<TEntity>().Add(entity);

            // After attaching, clear only those navigation properties that reference new (unpersisted) dependent instances.
            entity = BogusGenerator.ClearNewNavigationProperties(entity, Context);

            SaveChanges();
            return entity;
        }

        private (bool allPrimaryKeysSet, bool allForeignKeysSet) AreAllKeysSet<TEntity>(TEntity entity) where TEntity : class
        {
            var metadata = DependencyResolver.GetEntityMetadata().FirstOrDefault(em => em.EntityType == typeof(TEntity));
            if (metadata == null)
                return (false, false);

            bool allPrimaryKeysSet = metadata.PrimaryKeys.Count > 0;
            bool allForeignKeysSet = metadata.ForeignKeys.Count > 0;

            foreach (var key in metadata.PrimaryKeys)
            {
                var prop = typeof(TEntity).GetProperty(key);
                if(prop == null)
                {
                    allPrimaryKeysSet = false; break;
                }
                var value = prop.GetValue(entity);

                if(IsDefaultOrEmpty(value, prop.PropertyType))
                {
                    allPrimaryKeysSet = false; break;
                }
            }
            foreach (var fk in metadata.ForeignKeys)
            {
                foreach (var fkProp in fk.ForeignKeyProperties)
                {
                    var prop = typeof(TEntity).GetProperty(fkProp);
                    if (prop == null)
                    {
                        allForeignKeysSet = false; break;
                    }
                    var value = prop.GetValue(entity);
                    if (IsDefaultOrEmpty(value, prop.PropertyType))
                    {
                        allForeignKeysSet = false; break;
                    }
                }
            }
            return (allPrimaryKeysSet, allForeignKeysSet);
        }

        private static bool IsDefaultOrEmpty(object? value, Type type)
        {
            if(value == null) 
                return true;
            if (type.IsValueType)
            {
                return value.Equals(Activator.CreateInstance(type));
            }
            else if (type == typeof(string))
            {
                return string.IsNullOrEmpty(value as string);
            }
            else if(type == typeof(Guid))
            {
                return (Guid)value == Guid.Empty;
            }
            return false;
        }

        private string SerializeRecursiveEntity<TEntity>(TEntity entity) where TEntity : class
        {
            var json = JsonConvert.SerializeObject(entity, new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                PreserveReferencesHandling = PreserveReferencesHandling.None
            });
            return json;
        }


        public int SaveChanges() => Context.SaveChanges();

        /// <summary>
        /// Resolve some issues with the SQLite connection not closing properly with Files as DB source.
        /// </summary>
        public void CloseConnection()
        {
            _connection?.Close();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// Additional CloseConnection step to force threads to release lock on file Sqlite sources.
        /// </summary>
        public void CloseAllConnections()
        {
            SQLiteAsyncConnection.ResetPool();
        }

        /// <summary>
        /// Creates a new DbContext with shared connection and options to persist data.
        /// </summary>
        /// <returns></returns>
        public T CopyDbContext()
        {
            var args = new object[] { Options };
            return Activator.CreateInstance(typeof(T), args) as T;
        }
    }
}