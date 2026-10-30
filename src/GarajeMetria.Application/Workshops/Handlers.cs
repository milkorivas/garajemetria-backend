using FluentValidation;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Workshops;
using MediatR;

namespace GarajeMetria.Application.Workshops;

public sealed record ListWorkshopsQuery(int PageNumber, int PageSize, bool? IsActive) : IRequest<Outcome<WorkshopPage>>;

public sealed class ListWorkshopsValidator : AbstractValidator<ListWorkshopsQuery>
{
    public ListWorkshopsValidator(WorkshopCatalogOptions options)
    {
        RuleFor(x => x.PageNumber).InclusiveBetween(1, int.MaxValue);
        RuleFor(x => x.PageSize).InclusiveBetween(1, options.MaxPageSize);
        RuleFor(x => x).Must(x => (long)(x.PageNumber - 1) * x.PageSize <= int.MaxValue);
    }
}

public sealed class ListWorkshopsHandler(IWorkshopCatalogStore store)
    : IRequestHandler<ListWorkshopsQuery, Outcome<WorkshopPage>>
{
    public async Task<Outcome<WorkshopPage>> Handle(ListWorkshopsQuery request, CancellationToken cancellationToken)
    {
        var page = await store.ListAsync(request.PageNumber, request.PageSize, request.IsActive, cancellationToken);
        return page is null ? Outcome<WorkshopPage>.Fail(FailureCode.ServiceUnavailable) : Outcome<WorkshopPage>.Success(page);
    }
}

public sealed record SynchronizeWorkshopsCommand : IRequest<CatalogPublication>;

public sealed class SynchronizeWorkshopsHandler(IWorkshopSource source, IWorkshopCatalogStore store)
    : IRequestHandler<SynchronizeWorkshopsCommand, CatalogPublication>
{
    public async Task<CatalogPublication> Handle(SynchronizeWorkshopsCommand request, CancellationToken cancellationToken)
    {
        // Reserve an ordered version using the shared store before external I/O.
        var observation = await store.BeginObservationAsync(cancellationToken);
        var read = await source.ReadAsync(cancellationToken);
        if (!read.IsComplete || read.Items.Count == 0 && !read.EmptyConfirmed || !WorkshopCatalog.IsValid(read.Items))
            throw new InvalidOperationException("The workshop source did not supply a valid complete observation.");
        return await store.PublishAsync(observation, read.Items, cancellationToken);
    }
}
