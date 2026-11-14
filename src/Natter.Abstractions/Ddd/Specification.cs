using System.Linq.Expressions;

namespace Natter.Abstractions.Ddd;

/// <summary>
/// Specification pattern: composable, strongly typed business predicates.
/// Adapters may translate the expression tree to SQL or evaluate it in memory.
/// </summary>
public abstract class Specification<T>
{
    public abstract Expression<Func<T, bool>> ToExpression();

    public bool IsSatisfiedBy(T candidate) => ToExpression().Compile()(candidate);

    public Specification<T> And(Specification<T> other) => new AndSpecification<T>(this, other);

    public Specification<T> Or(Specification<T> other) => new OrSpecification<T>(this, other);

    public Specification<T> Not() => new NotSpecification<T>(this);

    private sealed class AndSpecification<TEntity>(Specification<TEntity> left, Specification<TEntity> right)
        : Specification<TEntity>
    {
        public override Expression<Func<TEntity, bool>> ToExpression()
        {
            var parameter = Expression.Parameter(typeof(TEntity), "x");
            var body = Expression.AndAlso(
                Expression.Invoke(left.ToExpression(), parameter),
                Expression.Invoke(right.ToExpression(), parameter));
            return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        }
    }

    private sealed class OrSpecification<TEntity>(Specification<TEntity> left, Specification<TEntity> right)
        : Specification<TEntity>
    {
        public override Expression<Func<TEntity, bool>> ToExpression()
        {
            var parameter = Expression.Parameter(typeof(TEntity), "x");
            var body = Expression.OrElse(
                Expression.Invoke(left.ToExpression(), parameter),
                Expression.Invoke(right.ToExpression(), parameter));
            return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        }
    }

    private sealed class NotSpecification<TEntity>(Specification<TEntity> inner)
        : Specification<TEntity>
    {
        public override Expression<Func<TEntity, bool>> ToExpression()
        {
            var parameter = Expression.Parameter(typeof(TEntity), "x");
            var body = Expression.Not(Expression.Invoke(inner.ToExpression(), parameter));
            return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        }
    }
}
