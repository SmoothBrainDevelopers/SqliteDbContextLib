using AutoPopulate;
using Bogus;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using SqliteDbContext.Interfaces;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SqliteDbContext.Generator
{
    /// <summary>
    /// Keeps track of generated IDs/keys per entity type.
    /// Handles auto-incrementing for numeric keys and retrieval of valid foreign keys.
    /// </summary>
    public class KeySeeder : IKeySeeder
    {
        private readonly Random _rng;
        private readonly DbContext _context;
        private readonly IDependencyResolver _dependencyResolver;
        private readonly IEntityGenerator _entityGenerator;
        private readonly ConcurrentDictionary<(Type, string), object> _currentKeys = new ConcurrentDictionary<(Type, string), object>();

        // Allow developers to override key fetching logic.
        public Func<Type, string, object> CustomKeyFetcher { get; set; }
        public bool AllowExistingForeignKeys { get; set; } = true;
        /// <summary>
        /// Chance (from 0.0 to 1.0) to use an existing dependent instance rather than generating a new one.
        /// </summary>
        public double ExistingReferenceChance { get; set; } = 0.7;

        private const int MaxRecursionDepth = 5;

        public KeySeeder(DbContext context, IDependencyResolver dependencyResolver, IEntityGenerator entityGenerator)
        {
            _context = context;
            _dependencyResolver = dependencyResolver;
            _entityGenerator = entityGenerator ?? new EntityGenerator();
            _rng = new Random();
        }

        /// <summary>
        /// Clears primary key and foreign key properties of the entity.
        /// </summary>
        public void ClearKeyProperties<T>(T entity, int recursionDepth = 0) where T : class
        {
            var meta = _dependencyResolver.GetEntityMetadata().FirstOrDefault(e => e.EntityType == typeof(T));
            if (meta == null) return;
            foreach (var keyProp in meta.PrimaryKeys)
            {
                var propInfo = typeof(T).GetProperty(keyProp);
                if (propInfo != null && propInfo.CanWrite)
                    propInfo.SetValue(entity, GetDefault(propInfo.PropertyType));
            }

            if(meta.ForeignKeys != null)
            {
                foreach(var fk in meta.ForeignKeys)
                {
                    foreach(var fkProp in fk.ForeignKeyProperties)
                    {
                        var propInfo = typeof(T).GetProperty(fkProp);
                        if (propInfo != null && propInfo.CanWrite)
                            propInfo.SetValue(entity, GetDefault(propInfo.PropertyType));
                    }
                }
            }
        }

        /// <summary>
        /// Clears virtual (non-collection) navigation properties by setting them to null.
        /// </summary>
        /// <param name="instance">The entity instance whose navigation properties should be cleared.</param>
        public void ClearNavigationReferences(object? instance)
        {
            if(instance == null) return;

            var type = instance.GetType();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite)
                    continue;

                // Skip strings.
                if (prop.PropertyType == typeof(string))
                    continue;

                // Skip collection types (IEnumerable but not string).
                if (typeof(IEnumerable).IsAssignableFrom(prop.PropertyType))
                    continue;

                var getter = prop.GetGetMethod();
                // Check if the getter is virtual and not final.
                if (getter != null && getter.IsVirtual && !getter.IsFinal)
                {
                    // Set this virtual navigation property to null.
                    prop.SetValue(instance, null);
                }
            }
        }

        /// <summary>
        /// Assigns primary and foreign keys to the entity.
        /// For foreign keys, if dependent instances exist, uses ExistingReferenceChance to decide whether to reuse or generate a new one.
        /// </summary>
        public void AssignKeys<T>(T entity, int recursionDepth = 0, bool allPrimaryKeysSet = false, bool allForeignKeysSet = false) where T : class
        {
            if (recursionDepth >= MaxRecursionDepth)
                throw new InvalidOperationException($"Maximum recursion depth reached for type {typeof(T).FullName}.");

            var meta = _dependencyResolver.GetEntityMetadata().FirstOrDefault(e => e.EntityType == typeof(T));
            if (meta == null) return;

            // Assign primary keys.
            if (!allPrimaryKeysSet)
            {
                int maxAttempts = 1000;
                int attempts = 0;
                var set = _context.Set<T>();
                var compositeKeyLambda = _dependencyResolver.GetCompositeKeyLambda<T>(meta.PrimaryKeys).Compile();

                object[] GetKeyValues(T e) => compositeKeyLambda(e) as object[];

                // Use BuildKeyEqualsLambda for optimized duplicate check
                var keyPropJoined = string.Join(",", meta.PrimaryKeys);
                var keyTypes = meta.PrimaryKeys.Select(k => typeof(T).GetProperty(k)?.PropertyType ?? typeof(object)).ToArray();
                // For composite keys, propType is typeof(object[])
                Type propType = keyTypes.Length == 1 ? keyTypes[0] : typeof(object[]);
                var keyEquals = BuildKeyEqualsLambda<T>(keyPropJoined, propType);

                // Replace the IsDuplicate() method in AssignKeys<T> with the following:

                bool IsDuplicate()
                {
                    var entityKeyValues = GetKeyValues(entity);
                    if (keyTypes.Length == 1)
                    {
                        // Single key: pass the value directly
                        return set.AsEnumerable().Any(e => keyEquals(e, entityKeyValues[0]));
                    }
                    else
                    {
                        // Composite key: pass the object[] as expected
                        return set.AsEnumerable().Any(e => keyEquals(e, entityKeyValues));
                    }
                }

                do
                {
                    // 1. Assign all primary key values (generate or fetch)
                    // Re-generate all primary key values
                    foreach (var keyProp in meta.PrimaryKeys)
                    {
                        var propInfo = typeof(T).GetProperty(keyProp);
                        if (propInfo != null && propInfo.CanWrite)
                        {
                            object newKey = CustomKeyFetcher != null
                                ? CustomKeyFetcher(typeof(T), keyProp)
                                : AutoIncrementKey(typeof(T), keyProp, propInfo.PropertyType);
                            propInfo.SetValue(entity, newKey);
                        }
                    }
                    attempts++;
                    if (attempts > maxAttempts)
                        throw new InvalidOperationException($"Unable to generate unique composite key for {typeof(T).Name} after {maxAttempts} attempts.");
                    // 2. Check for duplicate composite key
                } while (IsDuplicate());
            }

            if (allForeignKeysSet) return;

            // Process foreign keys.
            foreach (var foreign in meta.ForeignKeys)
            {
                foreach (var fkProp in foreign.ForeignKeyProperties)
                {
                    var propInfo = typeof(T).GetProperty(fkProp);
                    if (propInfo != null && propInfo.CanWrite)
                    {
                        // Find the dependent (principal) type for this foreign key.
                        var entityType = _context.Model.FindEntityType(typeof(T));
                        var foreignType = entityType?
                            .GetForeignKeys()
                            .FirstOrDefault(fk => fk.Properties.Any(p => p.Name == fkProp))
                            ?.PrincipalEntityType.ClrType;
                        if (foreignType != null && AllowExistingForeignKeys)
                        {
                            var set = GetQueryableForType(_context, foreignType);
                            int count = set.Cast<object>().Count();
                            if (count > 0)
                            {
                                if (_rng.NextDouble() < ExistingReferenceChance)
                                {
                                    // Choose an existing instance at random.
                                    var list = set.Cast<object>().ToList();
                                    var randomIndex = _rng.Next(list.Count);
                                    var randomInstance = list[randomIndex];
                                    propInfo.SetValue(entity, GetKeyValue(randomInstance));
                                    continue;
                                }
                                else
                                {
                                    // Generate a new dependent instance (recursively).
                                    var newDependent = GenerateDependentInstance(foreignType, recursionDepth + 1);
                                    propInfo.SetValue(entity, GetKeyValue(newDependent));
                                    continue;
                                }
                            }
                            else
                            {
                                // No existing instance; generate a new one.
                                var newDependent = GenerateDependentInstance(foreignType, recursionDepth + 1);
                                propInfo.SetValue(entity, GetKeyValue(newDependent));
                                continue;
                            }
                        }
                        // Fallback: assign an auto-incremented key.
                        propInfo.SetValue(entity, AutoIncrementKey(typeof(T), fkProp, propInfo.PropertyType));
                    }
                }
            }
        }

        // Helper to build and cache the lambda for duplicate key checking
        private static readonly ConcurrentDictionary<(Type, string, Type), Delegate> _keyEqualsLambdaCache = new();

        // Return a lambda that takes both entity and value (supports composite keys: value is object[])
        private static Func<T, object, bool> BuildKeyEqualsLambda<T>(string keyProp, Type propType)
        {
            var cacheKey = (typeof(T), keyProp, propType);
            if (!_keyEqualsLambdaCache.TryGetValue(cacheKey, out var cachedLambda))
            {
                var parameter = Expression.Parameter(typeof(T), "e");
                var valueParameter = Expression.Parameter(typeof(object), "val");

                // Support composite keys: keyProp can be comma-separated property names
                var keyProps = keyProp.Split(',').Select(p => p.Trim()).ToArray();
                Expression comparison;

                if (keyProps.Length == 1)
                {
                    // Single key
                    var property = Expression.Property(parameter, keyProps[0]);
                    Expression convertedValue;
                    if (propType.IsValueType && Nullable.GetUnderlyingType(propType) == null)
                    {
                        convertedValue = Expression.Unbox(valueParameter, propType);
                    }
                    else
                    {
                        convertedValue = Expression.Convert(valueParameter, propType);
                    }
                    comparison = Expression.Equal(property, convertedValue);
                }
                else
                {
                    // Composite key: valueParameter is object[]
                    var valueArray = Expression.Convert(valueParameter, typeof(object[]));
                    var equals = new List<Expression>();
                    for (int i = 0; i < keyProps.Length; i++)
                    {
                        var propInfo = typeof(T).GetProperty(keyProps[i]);
                        var property = Expression.Property(parameter, keyProps[i]);
                        var element = Expression.ArrayIndex(valueArray, Expression.Constant(i));
                        Expression convertedElement;
                        var type = propInfo.PropertyType;
                        if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
                        {
                            convertedElement = Expression.Unbox(element, type);
                        }
                        else
                        {
                            convertedElement = Expression.Convert(element, type);
                        }
                        equals.Add(Expression.Equal(property, convertedElement));
                    }
                    comparison = equals.Aggregate(Expression.AndAlso);
                }

                var lambda = Expression.Lambda<Func<T, object, bool>>(comparison, parameter, valueParameter);
                var compiled = lambda.Compile();
                _keyEqualsLambdaCache[cacheKey] = compiled;
                cachedLambda = compiled;
            }
            return (Func<T, object, bool>)cachedLambda;
        }

        /// <summary>
        /// Generates a new dependent instance for the specified type.
        /// This method is recursive—if the new instance has foreign keys, those will be processed (up to MaxRecursionDepth).
        /// </summary>
        private object GenerateDependentInstance(Type entityType, int recursionDepth = 0)
        {
            if (recursionDepth >= MaxRecursionDepth)
                throw new InvalidOperationException($"Maximum recursion depth reached when generating dependent instance for {entityType.FullName}.");

            var instance = _entityGenerator.CreateFake(entityType);
            // Clear virtual navigation (non-ICollection) properties so that foreign references do not override key assignments.
            ClearNavigationReferences(instance);
            // Clear and assign keys for the new instance (using the generic methods via reflection).
            var clearMethod = GetType().GetMethod("ClearKeyProperties", BindingFlags.Public | BindingFlags.Instance)
                              .MakeGenericMethod(entityType);
            clearMethod.Invoke(this, new object[] { instance, recursionDepth });
            var assignMethod = GetType().GetMethod("AssignKeys", BindingFlags.Public | BindingFlags.Instance)
                               .MakeGenericMethod(entityType);
            assignMethod.Invoke(this, new object[] { instance, recursionDepth, false, false });
            // Add the instance to the context.
            var setMethod = typeof(DbContext).GetMethod("Set", Type.EmptyTypes).MakeGenericMethod(entityType);
            var dbSet = setMethod.Invoke(_context, null);
            var addMethod = dbSet.GetType().GetMethod("Add", new Type[] { entityType });
            addMethod.Invoke(dbSet, new object[] { instance });
            _context.SaveChanges();
            return instance;
        }

        // ---------- Helper Methods ----------

        private object AutoIncrementKey(Type entityType, string propertyName, Type keyType)
        {
            var key = (entityType, propertyName);
            object currentVal = _currentKeys.ContainsKey(key) ? _currentKeys[key] : GetDefault(keyType);
            object newVal;
            if (keyType == typeof(int))
            {
                int curr = currentVal is int i ? i : 0;
                newVal = curr + 1;
            }
            else if (keyType == typeof(long))
            {
                long curr = currentVal is long l ? l : 0;
                newVal = curr + 1L;
            }
            else if (keyType == typeof(Guid))
            {
                newVal = Guid.NewGuid();
            }
            else if (keyType == typeof(string))
            {
                newVal = Guid.NewGuid().ToString();
            }
            else if (keyType == typeof(DateTime))
            {
                newVal = DateTime.UtcNow;
            }
            else
            {
                newVal = GetDefault(keyType);
            }
            _currentKeys[key] = newVal;
            return newVal;
        }

        private object GetKeyValue(object entity)
        {
            var key = _context.Model.FindEntityType(entity.GetType())?.FindPrimaryKey();
            if (key != null && key.Properties.Any())
            {
                var propName = key.Properties.First().Name;
                var prop = entity.GetType().GetProperty(propName);
                return prop?.GetValue(entity);
            }
            return null;
        }

        private object GetDefault(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

        private IQueryable GetQueryableForType(DbContext ctx, Type entityType)
        {
            var method = typeof(DbContext).GetMethod("Set", Type.EmptyTypes);
            var generic = method.MakeGenericMethod(entityType);
            return (IQueryable)generic.Invoke(ctx, null);
        }
    }
}