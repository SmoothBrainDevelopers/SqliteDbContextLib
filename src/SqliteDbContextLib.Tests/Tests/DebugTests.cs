using Microsoft.EntityFrameworkCore;
using SmoothBrainDevelopers.DataLayer.Test.Context;
using SmoothBrainDevelopers.DataLayer.Test.Domain;
using SqliteDbContext.Context;
//using SqliteDbContext.Debug;
using SqliteDbContext.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SqliteDbContextLib.Tests.Tests
{
    internal class DebugTests : TestBase
    {
        [Test]
        public void DisplayDependencies()
        {
            var dependencyResolver = new DependencyResolver(ctx);
            //Console.WriteLine(string.Join("\n", dependencyResolver.GetDependencyOrder().Select(x => x.Name)));
            Console.WriteLine(string.Join("\n", dependencyResolver.GetOrderedEntityTypes()));
        }

        [Test]
        public void DbContextDebuggerTest()
        {
            //var dbContextDebugger = new DbContextDebugger<TestDbContext>(context);

            //var entities = context.GenerateEntities<Purchase>(1);
            ////var entities = context.GenerateEntities<Purchase>(5);
            //var keySeeder = context.KeySeeder;

            //dbContextDebugger.DumpKeySeederStatus(keySeeder);
            //var customers = ctx.Customers.ToList();
            //var regions = ctx.Regions.ToList();
            //var stores = ctx.Stores.ToList();
            //var products = ctx.Products.ToList();
            //var purchases = ctx.Purchases.ToList();

            //Assert.AreEqual(1, customers.Count);
            //Assert.AreEqual(1, regions.Count);
            //Assert.AreEqual(1, stores.Count);
            //Assert.AreEqual(1, products.Count);
            //Assert.AreEqual(1, purchases.Count);
        }
    }
}
