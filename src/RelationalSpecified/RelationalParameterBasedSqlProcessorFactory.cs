﻿#if EFCORE50

namespace Microsoft.EntityFrameworkCore.Query
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using Microsoft.EntityFrameworkCore.Bulk;
    using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

#if SQL_SERVER
    using ThisSqlNullabilityProcessor = Microsoft.EntityFrameworkCore.Query.SqlNullabilityProcessor;
    using ThisParameterBasedSqlProcessor = Microsoft.EntityFrameworkCore.SqlServer.Query.Internal.SqlServerParameterBasedSqlProcessor;
    using ThisParameterBasedSqlProcessorFactory = Microsoft.EntityFrameworkCore.SqlServer.Query.Internal.SqlServerParameterBasedSqlProcessorFactory;
#elif POSTGRE_SQL
    using ThisSqlNullabilityProcessor = Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal.NpgsqlSqlNullabilityProcessor;
    using ThisParameterBasedSqlProcessor = Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal.NpgsqlParameterBasedSqlProcessor;
    using ThisParameterBasedSqlProcessorFactory = Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal.NpgsqlParameterBasedSqlProcessorFactory;
#endif

    public class XysSqlNullabilityProcessor : ThisSqlNullabilityProcessor
    {
        public XysSqlNullabilityProcessor(
            RelationalParameterBasedSqlProcessorDependencies dependencies,
            bool useRelationalNulls)
            : base(dependencies, useRelationalNulls)
        {
        }

        protected override TableExpressionBase Visit(TableExpressionBase tableExpressionBase)
        {
            switch (tableExpressionBase)
            {
                case ValuesExpression values:
                    DoNotCache();
                    return values;

                case DeleteExpression delete:
                    return Visit(delete);

                case UpdateExpression update:
                    return Visit(update);

                case SelectIntoExpression selectInto:
                    return Visit(selectInto);

                case UpsertExpression upsert:
                    return Visit(upsert);
            }

            return base.Visit(tableExpressionBase);
        }

        protected virtual IReadOnlyList<ProjectionExpression> Visit(IReadOnlyList<ProjectionExpression> projections)
        {
            if (projections == null) return null;

            bool changed = false;
            var fields = projections.ToList();
            for (int i = 0; i < fields.Count; i++)
            {
                var newExpr = Visit(fields[i].Expression, allowOptimizedExpansion: true, out _);
                fields[i] = fields[i].Update(newExpr);
                changed = changed || fields[i] != projections[i];
            }

            return changed ? fields : projections;
        }

        protected virtual IReadOnlyList<TableExpressionBase> Visit(IReadOnlyList<TableExpressionBase> origTables)
        {
            bool changed = false;
            var tables = origTables.ToList();
            for (int i = 0; i < tables.Count; i++)
            {
                tables[i] = Visit(tables[i]);
                changed = changed || tables[i] != origTables[i];
            }

            return changed ? tables : origTables;
        }

        protected virtual DeleteExpression Visit(DeleteExpression deleteExpression)
        {
            var mainTable = (TableExpression)Visit(deleteExpression.Table);
            bool changed = mainTable == deleteExpression.Table;

            var joinedTables = Visit(deleteExpression.JoinedTables);
            changed |= joinedTables != deleteExpression.JoinedTables;

            var predicate = Visit(deleteExpression.Predicate, allowOptimizedExpansion: true, out _);
            changed |= predicate != deleteExpression.Predicate;

            if (TryGetBoolConstantValue(predicate) == true)
            {
                predicate = null;
                changed = true;
            }

            return changed
                ? new DeleteExpression(mainTable, predicate, joinedTables)
                : deleteExpression;
        }

        protected virtual UpdateExpression Visit(UpdateExpression updateExpression)
        {
            var expandedTable = updateExpression.ExpandedTable == null ? null : (TableExpression)Visit(updateExpression.ExpandedTable);
            bool changed = expandedTable != updateExpression.ExpandedTable;

            var predicate = Visit(updateExpression.Predicate, allowOptimizedExpansion: true, out _);
            changed |= predicate != updateExpression.Predicate;

            var fields = Visit(updateExpression.Fields);
            changed |= fields == updateExpression.Fields;

            var tables = Visit(updateExpression.Tables);
            changed |= tables == Visit(updateExpression.Tables);

            return changed
                ? new UpdateExpression(updateExpression.Expanded, expandedTable, predicate, fields, tables)
                : updateExpression;
        }

        protected virtual SelectIntoExpression Visit(SelectIntoExpression selectIntoExpression)
        {
            var expression = base.Visit(selectIntoExpression.Expression);
            return expression != selectIntoExpression.Expression
                ? new SelectIntoExpression(selectIntoExpression.TableName, selectIntoExpression.Schema, expression)
                : selectIntoExpression;
        }

        protected virtual UpsertExpression Visit(UpsertExpression upsertExpression)
        {
            var targetTable = (TableExpression)Visit(upsertExpression.TargetTable);
            bool changed = targetTable != upsertExpression.TargetTable;

            var sourceTable = Visit(upsertExpression.SourceTable);
            changed = changed || sourceTable != upsertExpression.SourceTable;

            var onConflictUpdate = Visit(upsertExpression.OnConflictUpdate);
            changed = changed || onConflictUpdate != upsertExpression.OnConflictUpdate;

            var columns = Visit(upsertExpression.Columns);
            changed = changed || columns != upsertExpression.Columns;

            return changed
                ? new UpsertExpression(targetTable, sourceTable, columns, onConflictUpdate, upsertExpression.ConflictConstraintName)
                : upsertExpression;
        }

        private static bool? TryGetBoolConstantValue(SqlExpression expression)
            => expression is SqlConstantExpression constantExpression
                && constantExpression.Value is bool boolValue
                    ? boolValue
                    : (bool?)null;

        protected override SqlExpression VisitCustomSqlExpression(
            SqlExpression sqlExpression,
            bool allowOptimizedExpansion,
            out bool nullable)
        {
            if (sqlExpression is AffectedRowsExpression)
            {
                nullable = false;
                return sqlExpression;
            }

            if (sqlExpression is ExcludedTableColumnExpression excluded)
            {
                nullable = excluded.IsNullable;
                return excluded;
            }

            return base.VisitCustomSqlExpression(
                sqlExpression,
                allowOptimizedExpansion,
                out nullable);
        }
    }

    public class XysParameterBasedSqlProcessor : ThisParameterBasedSqlProcessor
    {
        public XysParameterBasedSqlProcessor(
            RelationalParameterBasedSqlProcessorDependencies dependencies,
            bool useRelationalNulls)
            : base(dependencies, useRelationalNulls)
        {
        }

        protected override SelectExpression ProcessSqlNullability(
            SelectExpression selectExpression,
            IReadOnlyDictionary<string, object> parametersValues,
            out bool canCache)
        {
            Check.NotNull(selectExpression, nameof(selectExpression));
            Check.NotNull(parametersValues, nameof(parametersValues));

            return new XysSqlNullabilityProcessor(Dependencies, UseRelationalNulls)
                .Process(selectExpression, parametersValues, out canCache);
        }

        protected virtual SelectExpression ProcessValuesExpansion(
            SelectExpression selectExpression,
            IReadOnlyDictionary<string, object> parametersValues)
        {
            return new ValuesExpressionParameterExpandingVisitor(parametersValues)
                .VisitAndConvert(selectExpression, null);
        }

        public override SelectExpression Optimize(
            SelectExpression selectExpression,
            IReadOnlyDictionary<string, object> parametersValues,
            out bool canCache)
        {
            selectExpression = base.Optimize(selectExpression, parametersValues, out canCache);
            selectExpression = ProcessValuesExpansion(selectExpression, parametersValues);
            return selectExpression;
        }
    }

    public class XysParameterBasedSqlProcessorFactory :
        IRelationalParameterBasedSqlProcessorFactory,
        IServiceAnnotation<IRelationalParameterBasedSqlProcessorFactory, ThisParameterBasedSqlProcessorFactory>
    {
        private readonly RelationalParameterBasedSqlProcessorDependencies _dependencies;

        public XysParameterBasedSqlProcessorFactory(
            RelationalParameterBasedSqlProcessorDependencies dependencies)
        {
            Check.NotNull(dependencies, nameof(dependencies));

            _dependencies = dependencies;
        }

        public virtual RelationalParameterBasedSqlProcessor Create(bool useRelationalNulls)
            => new XysParameterBasedSqlProcessor(_dependencies, useRelationalNulls);
    }
}

#endif
