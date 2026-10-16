using MediatR;

namespace EveOPreview.Mediator.Messages;

/// <summary>Makes one character's preview border color and thumbnail augments the shared look of every preview.</summary>
public sealed record UsePreviewLookForAll(string Title) : IRequest;
