using FluentValidation;
using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Validators;

// Cook log (plan-page redesign / roadmap spec 2). Null is legal and means "clear the note",
// so the only rule is the column width — CookLog.Note is HasMaxLength(500).
public class UpdateCookNoteRequestValidator : AbstractValidator<UpdateCookNoteRequest>
{
    public UpdateCookNoteRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(500);
    }
}
