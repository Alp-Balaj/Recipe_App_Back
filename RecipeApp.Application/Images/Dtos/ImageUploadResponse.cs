namespace RecipeApp.Application.Images.Dtos;

// Wire response of POST /images (social-feed cp04): the URL the stored image is served
// from — a relative path (/images/<guid>.<ext>) the same host serves; the frontend drops
// it straight into Recipe.ImageUrl on create/update (those flows are untouched).
public record ImageUploadResponse(string Url);
