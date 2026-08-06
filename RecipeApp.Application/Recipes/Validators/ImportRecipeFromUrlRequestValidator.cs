using FluentValidation;
using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Validators;

// Stream L. A deliberately SHALLOW validator: it catches the shapes that are wrong on their
// face, and nothing more.
//
// Everything that actually decides whether a URL may be fetched — the scheme, embedded
// credentials, and above all where the host resolves to — lives in the fetcher and its connect
// callback, and must NOT be duplicated here. Two reasons, and the second is the one that
// matters:
//
//   1. A validator runs once, before the request reaches the service. The address policy has
//      to run again on every redirect hop, so a check here could never be the whole of it.
//   2. A security check that exists in two places is a security check that will disagree with
//      itself. If this file grew a "reject private addresses" rule, a future edit to one copy
//      would silently leave the other permissive — and the permissive one is the one attached
//      to the socket.
//
// So: length and non-emptiness, so an obviously junk request gets a clean 400 without opening
// a socket. The real gate is elsewhere and is meant to be.
public class ImportRecipeFromUrlRequestValidator : AbstractValidator<ImportRecipeFromUrlRequest>
{
    // Browsers cap URLs around 2 KB and no recipe page needs more; an unbounded string here
    // would otherwise be a free way to make the server parse megabytes.
    public const int MaxUrlLength = 2048;

    public ImportRecipeFromUrlRequestValidator()
    {
        RuleFor(x => x.Url)
            .NotEmpty()
            .MaximumLength(MaxUrlLength);
    }
}
