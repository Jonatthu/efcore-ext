﻿using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Microsoft.EntityFrameworkCore.Bulk
{
    public static class RelationalExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [DebuggerStepThrough]
        public static Dictionary<string, string> GetColumns(
            this IEntityType entityType)
        {
            var normalProps = entityType.GetProperties()
                .Select(p => (p.Name, p.GetColumnName()));
            var ownerProps =
                from nav in entityType.GetNavigations()
                where nav.GetTargetType().IsOwned()
                let prop = nav.PropertyInfo
                from col in nav.GetTargetType().GetProperties()
                select ($"{prop.Name}.{col.Name}", col.GetColumnName());
            return normalProps.Concat(ownerProps)
                .ToDictionary(k => k.Item1, v => v.Item2);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [DebuggerStepThrough]
        public static Dictionary<string, ValueConverter> GetValueConverters(
            this IEntityType entityType)
        {
            var normalProps =
                from p in entityType.GetProperties()
                let conv = p.GetValueConverter()
                where conv != null
                select (p.GetColumnName(), conv);
            var ownerProps =
                from nav in entityType.GetNavigations()
                where nav.GetTargetType().IsOwned()
                let prop = nav.PropertyInfo
                from col in nav.GetTargetType().GetProperties()
                let conv = col.GetValueConverter()
                where conv != null
                select (col.GetColumnName(), conv);
            return normalProps.Concat(ownerProps)
                .Distinct()
                .ToDictionary(k => k.Item1, v => v.Item2);
        }
    }

    internal class RelationalBatchDbContextOptionsExtension<TNewFactory, TOldFactory, TProvider> :
        IDbContextOptionsExtension
        where TOldFactory : class, IQuerySqlGeneratorFactory
        where TNewFactory : class, IEnhancedQuerySqlGeneratorFactory<TOldFactory>
        where TProvider : RelationalBatchOperationProvider
    {
        private DbContextOptionsExtensionInfo _info;
        private readonly string _name;

        public RelationalBatchDbContextOptionsExtension(string name)
        {
            _name = name;
        }

        public DbContextOptionsExtensionInfo Info =>
            _info ??= new BatchDbContextOptionsExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
        {
            var sd = services.FirstOrDefault(d => d.ServiceType == typeof(IQuerySqlGeneratorFactory));
            if (sd?.ImplementationType != typeof(TOldFactory))
                throw new InvalidOperationException("No such IQuerySqlGeneratorFactory.");
            services[services.IndexOf(sd)] = ServiceDescriptor.Singleton(sd.ServiceType, typeof(TNewFactory));

            services.AddSingleton<IBatchOperationProvider, TProvider>();
        }

        public void Validate(IDbContextOptions options)
        {
        }

        private class BatchDbContextOptionsExtensionInfo : DbContextOptionsExtensionInfo
        {
            private readonly string _name;

            public BatchDbContextOptionsExtensionInfo(
                RelationalBatchDbContextOptionsExtension<TNewFactory, TOldFactory, TProvider> extension) : base(extension)
            {
                _name = extension._name;
                LogFragment = $"using {_name} ";
            }

            public override bool IsDatabaseProvider => false;

            public override string LogFragment { get; }

            public override long GetServiceProviderHashCode() => 0;

            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
                => debugInfo[_name] = "1";
        }
    }
}