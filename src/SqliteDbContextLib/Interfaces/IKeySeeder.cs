using AutoPopulate;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SqliteDbContext.Interfaces
{
    public interface IKeySeeder
    {
        public Func<Type, string, object> CustomKeyFetcher { get; set; }
        public bool AllowExistingForeignKeys { get; set; }
        public double ExistingReferenceChance { get; set; }
        public void ClearKeyProperties<T>(T entity, int recursionDepth = 0) where T : class;
        public void ClearNavigationReferences(object instance);
        public void AssignKeys<T>(T entity, int recursionDepth = 0, bool allPrimaryKeysSet = false, bool allForeignKeysSet = false) where T : class;
    }
}
