#!/usr/bin/env node
// Demo-content seeder (publish cp1). Talks EXCLUSIVELY to the public API — real
// registration (correct password hashes), real /images uploads (land in R2 in prod),
// real recipe/social endpoints (validation + gamification fire like production traffic).
// Never touches the database directly, so it works against any environment unchanged.
//
// Usage:
//   node tools/seed/seed.mjs <baseUrl> [--images <dir>]
//   node tools/seed/seed.mjs https://recipeapp-production-6009.up.railway.app --images "C:\path\to\images"
//
// Re-runnable: credentials are saved to seed-credentials.json (git-ignored) next to this
// script; on a re-run existing users log in instead of re-registering. Auth calls are
// paced under the API's 10/min-per-IP auth budget; a 429 anywhere waits out the window.

import { readFile, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const credsPath = path.join(here, "seed-credentials.json");

const [baseUrlArg, ...rest] = process.argv.slice(2);
if (!baseUrlArg) {
  console.error("Usage: node tools/seed/seed.mjs <baseUrl> [--images <dir>]");
  process.exit(1);
}
const baseUrl = baseUrlArg.replace(/\/+$/, "");
const imagesDir = rest.includes("--images") ? rest[rest.indexOf("--images") + 1] : null;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// --- API helpers ------------------------------------------------------------------

async function api(pathname, { method = "GET", token, body, form } = {}) {
  for (let attempt = 1; ; attempt++) {
    const headers = {};
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body) headers["Content-Type"] = "application/json";
    const res = await fetch(`${baseUrl}${pathname}`, {
      method,
      headers,
      body: form ?? (body ? JSON.stringify(body) : undefined),
    });
    if (res.status === 429 && attempt <= 3) {
      console.log(`    rate limited on ${pathname} — waiting 61s (attempt ${attempt}/3)...`);
      await sleep(61_000);
      continue;
    }
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* non-JSON body */ }
    return { status: res.status, json, text };
  }
}

// --- demo data --------------------------------------------------------------------

const USERS = [
  { username: "chef_maria",    email: "chef.maria@example.com",    bioName: "Maria" },
  { username: "pasta_paolo",   email: "pasta.paolo@example.com",   bioName: "Paolo" },
  { username: "spice_amara",   email: "spice.amara@example.com",   bioName: "Amara" },
  { username: "green_lena",    email: "green.lena@example.com",    bioName: "Lena" },
  { username: "grill_tomas",   email: "grill.tomas@example.com",   bioName: "Tomas" },
  { username: "brunch_sofie",  email: "brunch.sofie@example.com",  bioName: "Sofie" },
];

// image: filename inside --images dir (jpeg/png/webp only — the API magic-byte-checks).
const RECIPES = [
  {
    owner: "chef_maria", image: "Fish-Tacos.jpg",
    title: "Crispy Fish Tacos with Lime Crema",
    description: "Golden fried white fish tucked into warm corn tortillas with crunchy cabbage slaw and a tangy lime crema. Street-food energy, weeknight effort.",
    prepTimeMinutes: 20, cookTimeMinutes: 15, servings: 4, difficulty: "Medium",
    cuisineType: "Mexican", caloriesPerServing: 480, tags: ["tacos", "fish", "dinner", "weeknight"],
    ingredients: [
      { name: "white fish fillets", quantity: 500, unit: "g" },
      { name: "corn tortillas", quantity: 8, unit: "pieces" },
      { name: "red cabbage", quantity: 0.25, unit: "head" },
      { name: "sour cream", quantity: 120, unit: "ml" },
      { name: "lime", quantity: 2, unit: "pieces" },
      { name: "flour", quantity: 100, unit: "g" },
    ],
    steps: [
      { stepNumber: 1, description: "Slice the cabbage thinly and toss with juice of one lime and a pinch of salt." },
      { stepNumber: 2, description: "Whisk sour cream with the remaining lime juice and zest into a crema." },
      { stepNumber: 3, description: "Dredge fish pieces in seasoned flour and fry until golden.", timerSeconds: 360 },
      { stepNumber: 4, description: "Warm the tortillas, then build: fish, slaw, crema, hot sauce to taste." },
    ],
  },
  {
    owner: "chef_maria", image: "ratatouille.jpg",
    title: "Sunday Ratatouille",
    description: "Slow-simmered Provençal vegetables — eggplant, zucchini, peppers and tomato — layered and baked until silky. Even better the next day.",
    prepTimeMinutes: 30, cookTimeMinutes: 60, servings: 6, difficulty: "Medium",
    cuisineType: "French", caloriesPerServing: 210, tags: ["vegetarian", "vegetables", "baked", "meal-prep"],
    ingredients: [
      { name: "eggplant", quantity: 1, unit: "pieces" },
      { name: "zucchini", quantity: 2, unit: "pieces" },
      { name: "bell pepper", quantity: 2, unit: "pieces" },
      { name: "tomatoes", quantity: 6, unit: "pieces" },
      { name: "olive oil", quantity: 4, unit: "tbsp" },
      { name: "herbes de Provence", quantity: 1, unit: "tbsp" },
    ],
    steps: [
      { stepNumber: 1, description: "Slice all vegetables into thin rounds." },
      { stepNumber: 2, description: "Simmer a quick tomato base with olive oil and herbs.", timerSeconds: 600 },
      { stepNumber: 3, description: "Layer the vegetable slices over the base in a spiral." },
      { stepNumber: 4, description: "Bake at 180°C until tender and bubbling.", timerSeconds: 3600 },
    ],
  },
  {
    owner: "pasta_paolo", image: "lasagna.jpg",
    title: "Nonna's Beef Lasagna",
    description: "Layers of slow ragù, béchamel and fresh pasta sheets under a bronzed parmesan crust. The one dish that ends every family argument.",
    prepTimeMinutes: 45, cookTimeMinutes: 50, servings: 8, difficulty: "Hard",
    cuisineType: "Italian", caloriesPerServing: 620, tags: ["pasta", "beef", "comfort-food", "family"],
    ingredients: [
      { name: "ground beef", quantity: 600, unit: "g" },
      { name: "lasagna sheets", quantity: 12, unit: "pieces" },
      { name: "crushed tomatoes", quantity: 800, unit: "g" },
      { name: "milk", quantity: 700, unit: "ml" },
      { name: "butter", quantity: 60, unit: "g" },
      { name: "parmesan", quantity: 120, unit: "g" },
    ],
    steps: [
      { stepNumber: 1, description: "Brown the beef, add tomatoes, and simmer the ragù low and slow.", timerSeconds: 2700 },
      { stepNumber: 2, description: "Make the béchamel: melt butter, whisk in flour, then milk until thick." },
      { stepNumber: 3, description: "Layer ragù, béchamel and pasta sheets; repeat, finishing with parmesan." },
      { stepNumber: 4, description: "Bake at 190°C until deeply golden.", timerSeconds: 3000 },
      { stepNumber: 5, description: "Rest 15 minutes before cutting — it holds its shape.", timerSeconds: 900 },
    ],
  },
  {
    owner: "pasta_paolo", image: "sweet-chicken-spaghetti.jpg",
    title: "Sweet Chili Chicken Spaghetti",
    description: "Weeknight fusion: spaghetti tossed with seared chicken in a sticky sweet-chili glaze, scallions and toasted sesame.",
    prepTimeMinutes: 15, cookTimeMinutes: 20, servings: 4, difficulty: "Easy",
    cuisineType: "Fusion", caloriesPerServing: 540, tags: ["pasta", "chicken", "weeknight", "spicy"],
    ingredients: [
      { name: "spaghetti", quantity: 400, unit: "g" },
      { name: "chicken breast", quantity: 400, unit: "g" },
      { name: "sweet chili sauce", quantity: 120, unit: "ml" },
      { name: "soy sauce", quantity: 3, unit: "tbsp" },
      { name: "scallions", quantity: 4, unit: "pieces" },
      { name: "sesame seeds", quantity: 1, unit: "tbsp" },
    ],
    steps: [
      { stepNumber: 1, description: "Cook the spaghetti one minute shy of al dente.", timerSeconds: 480 },
      { stepNumber: 2, description: "Sear bite-size chicken pieces until golden.", timerSeconds: 420 },
      { stepNumber: 3, description: "Add sauces, toss in pasta with a splash of pasta water, finish with scallions and sesame." },
    ],
  },
  {
    owner: "pasta_paolo", image: "meatballs.jpg",
    title: "Classic Pork & Beef Meatballs",
    description: "Tender two-meat meatballs simmered in tomato sugo — pile them on pasta, bread, or eat them straight out of the pan.",
    prepTimeMinutes: 25, cookTimeMinutes: 35, servings: 6, difficulty: "Medium",
    cuisineType: "Italian", caloriesPerServing: 450, tags: ["beef", "pork", "comfort-food", "meal-prep"],
    ingredients: [
      { name: "ground beef", quantity: 400, unit: "g" },
      { name: "ground pork", quantity: 400, unit: "g" },
      { name: "breadcrumbs", quantity: 80, unit: "g" },
      { name: "egg", quantity: 2, unit: "pieces" },
      { name: "crushed tomatoes", quantity: 800, unit: "g" },
      { name: "garlic", quantity: 3, unit: "cloves" },
    ],
    steps: [
      { stepNumber: 1, description: "Mix meats, breadcrumbs, egg and seasoning; roll into balls without overworking." },
      { stepNumber: 2, description: "Brown the meatballs in batches.", timerSeconds: 480 },
      { stepNumber: 3, description: "Simmer in the tomato sugo until cooked through.", timerSeconds: 1500 },
    ],
  },
  {
    owner: "spice_amara", image: "Taiwanese-Three-Cup-Chicken.webp",
    title: "Three-Cup Chicken (San Bei Ji)",
    description: "Taiwan's glossy classic: chicken braised in equal parts soy, sesame oil and rice wine, finished with a mountain of Thai basil.",
    prepTimeMinutes: 15, cookTimeMinutes: 25, servings: 3, difficulty: "Medium",
    cuisineType: "Taiwanese", caloriesPerServing: 510, tags: ["chicken", "asian", "one-pot", "spicy"],
    ingredients: [
      { name: "chicken thighs", quantity: 600, unit: "g" },
      { name: "soy sauce", quantity: 60, unit: "ml" },
      { name: "toasted sesame oil", quantity: 60, unit: "ml" },
      { name: "rice wine", quantity: 60, unit: "ml" },
      { name: "ginger", quantity: 8, unit: "slices" },
      { name: "thai basil", quantity: 1, unit: "bunch" },
    ],
    steps: [
      { stepNumber: 1, description: "Sizzle ginger slices in sesame oil until fragrant and curling." },
      { stepNumber: 2, description: "Add chicken and brown hard on all sides.", timerSeconds: 420 },
      { stepNumber: 3, description: "Pour in soy and rice wine; braise uncovered until glossy.", timerSeconds: 900 },
      { stepNumber: 4, description: "Kill the heat, fold through the basil, serve over rice." },
    ],
  },
  {
    owner: "spice_amara", image: "hot-wings.jpg",
    title: "Sticky Gochujang Hot Wings",
    description: "Double-baked wings lacquered with a gochujang-honey glaze — crackly skin, slow heat, zero deep fryer.",
    prepTimeMinutes: 10, cookTimeMinutes: 50, servings: 4, difficulty: "Easy",
    cuisineType: "Korean", caloriesPerServing: 560, tags: ["chicken", "spicy", "party", "baked"],
    ingredients: [
      { name: "chicken wings", quantity: 1, unit: "kg" },
      { name: "gochujang", quantity: 3, unit: "tbsp" },
      { name: "honey", quantity: 2, unit: "tbsp" },
      { name: "soy sauce", quantity: 2, unit: "tbsp" },
      { name: "baking powder", quantity: 1, unit: "tbsp" },
      { name: "garlic", quantity: 2, unit: "cloves" },
    ],
    steps: [
      { stepNumber: 1, description: "Pat wings bone dry, toss with baking powder and salt." },
      { stepNumber: 2, description: "Bake at 220°C, flipping halfway, until crackly.", timerSeconds: 2700 },
      { stepNumber: 3, description: "Simmer the glaze, toss the hot wings through it, and serve immediately." },
    ],
  },
  {
    owner: "green_lena", image: "avocado-toast.jpg",
    title: "Chili-Crunch Avocado Toast",
    description: "Sourdough, smashed avocado, jammy egg and a spoon of chili crisp. Five minutes of effort, all-day smugness.",
    prepTimeMinutes: 5, cookTimeMinutes: 7, servings: 1, difficulty: "Easy",
    cuisineType: "Breakfast", caloriesPerServing: 380, tags: ["breakfast", "vegetarian", "quick", "toast"],
    ingredients: [
      { name: "sourdough bread", quantity: 2, unit: "slices" },
      { name: "avocado", quantity: 1, unit: "pieces" },
      { name: "egg", quantity: 1, unit: "pieces" },
      { name: "chili crisp", quantity: 1, unit: "tbsp" },
      { name: "lemon", quantity: 0.5, unit: "pieces" },
    ],
    steps: [
      { stepNumber: 1, description: "Boil the egg to jammy — straight from the fridge into boiling water.", timerSeconds: 405 },
      { stepNumber: 2, description: "Toast the sourdough dark; smash avocado with lemon and salt." },
      { stepNumber: 3, description: "Assemble: avocado, halved egg, chili crisp, flaky salt." },
    ],
  },
  {
    owner: "green_lena", image: "garlic-chicken-with-spinach.jpg",
    title: "Garlic Butter Chicken with Wilted Spinach",
    description: "Pan-seared chicken in a garlic butter sauce with spinach folded in at the last minute — a one-pan dinner in under half an hour.",
    prepTimeMinutes: 10, cookTimeMinutes: 18, servings: 2, difficulty: "Easy",
    cuisineType: "European", caloriesPerServing: 430, tags: ["chicken", "low-carb", "one-pot", "weeknight"],
    ingredients: [
      { name: "chicken breast", quantity: 2, unit: "pieces" },
      { name: "baby spinach", quantity: 200, unit: "g" },
      { name: "butter", quantity: 40, unit: "g" },
      { name: "garlic", quantity: 4, unit: "cloves" },
      { name: "chicken stock", quantity: 100, unit: "ml" },
    ],
    steps: [
      { stepNumber: 1, description: "Season and sear the chicken until golden on both sides.", timerSeconds: 600 },
      { stepNumber: 2, description: "Add butter and garlic; baste until the garlic is nutty." },
      { stepNumber: 3, description: "Splash in stock, fold in spinach off the heat until just wilted." },
    ],
  },
  {
    owner: "grill_tomas", image: "drumsticks.jpg",
    title: "Smoky Paprika Drumsticks",
    description: "Oven drumsticks with a smoked-paprika rub that tastes like the grill did the work. Crowd food on a sheet pan.",
    prepTimeMinutes: 10, cookTimeMinutes: 40, servings: 4, difficulty: "Easy",
    cuisineType: "American", caloriesPerServing: 390, tags: ["chicken", "baked", "family", "budget"],
    ingredients: [
      { name: "chicken drumsticks", quantity: 8, unit: "pieces" },
      { name: "smoked paprika", quantity: 2, unit: "tbsp" },
      { name: "brown sugar", quantity: 1, unit: "tbsp" },
      { name: "garlic powder", quantity: 1, unit: "tsp" },
      { name: "olive oil", quantity: 2, unit: "tbsp" },
    ],
    steps: [
      { stepNumber: 1, description: "Rub drumsticks with oil and the spice mix; rest 10 minutes.", timerSeconds: 600 },
      { stepNumber: 2, description: "Roast at 200°C, turning once, until the skin is burnished.", timerSeconds: 2400 },
    ],
  },
  {
    owner: "grill_tomas", image: "meatball-sandwich.jpg",
    title: "Meatball Sub, Game-Day Edition",
    description: "Leftover meatballs reborn: toasted roll, marinara, molten provolone, torn basil. Napkins non-negotiable.",
    prepTimeMinutes: 10, cookTimeMinutes: 12, servings: 2, difficulty: "Easy",
    cuisineType: "American", caloriesPerServing: 680, tags: ["sandwich", "beef", "comfort-food", "party"],
    ingredients: [
      { name: "cooked meatballs", quantity: 8, unit: "pieces" },
      { name: "sub rolls", quantity: 2, unit: "pieces" },
      { name: "marinara sauce", quantity: 250, unit: "ml" },
      { name: "provolone", quantity: 4, unit: "slices" },
      { name: "basil", quantity: 6, unit: "leaves" },
    ],
    steps: [
      { stepNumber: 1, description: "Reheat meatballs in marinara until bubbling.", timerSeconds: 480 },
      { stepNumber: 2, description: "Split and toast the rolls; load with meatballs and sauce." },
      { stepNumber: 3, description: "Top with provolone and broil until molten.", timerSeconds: 180 },
    ],
  },
  {
    owner: "brunch_sofie", image: "pasta-with-chicken.jpg",
    title: "Creamy Lemon Chicken Pasta",
    description: "Silky lemon-cream sauce clinging to ribbons of pasta and seared chicken — brunch-table fancy, pantry-simple.",
    prepTimeMinutes: 12, cookTimeMinutes: 18, servings: 3, difficulty: "Medium",
    cuisineType: "Italian", caloriesPerServing: 590, tags: ["pasta", "chicken", "creamy", "date-night"],
    ingredients: [
      { name: "fettuccine", quantity: 300, unit: "g" },
      { name: "chicken breast", quantity: 300, unit: "g" },
      { name: "cream", quantity: 200, unit: "ml" },
      { name: "lemon", quantity: 1, unit: "pieces" },
      { name: "parmesan", quantity: 60, unit: "g" },
    ],
    steps: [
      { stepNumber: 1, description: "Cook the pasta; reserve a cup of pasta water.", timerSeconds: 600 },
      { stepNumber: 2, description: "Sear sliced chicken; add cream, lemon zest and juice." },
      { stepNumber: 3, description: "Toss pasta through the sauce with parmesan, loosening with pasta water." },
    ],
  },
];

// Social graph: follower -> followed usernames.
const FOLLOWS = {
  chef_maria: ["pasta_paolo", "spice_amara", "green_lena"],
  pasta_paolo: ["chef_maria", "grill_tomas"],
  spice_amara: ["chef_maria", "green_lena", "brunch_sofie"],
  green_lena: ["spice_amara", "brunch_sofie"],
  grill_tomas: ["pasta_paolo", "chef_maria", "spice_amara"],
  brunch_sofie: ["green_lena", "chef_maria", "pasta_paolo"],
};

const COMMENTS = [
  "Made this tonight — absolute keeper!",
  "The timer on step 2 is spot on. Turned out perfect.",
  "Swapped in tofu and it still slapped.",
  "My kids asked for seconds. That never happens.",
  "This is going straight into the weekly rotation.",
  "Underrated tip: double the sauce. Thank me later.",
];

// --- main -------------------------------------------------------------------------

const contentTypeFor = (file) =>
  file.endsWith(".webp") ? "image/webp" : file.endsWith(".png") ? "image/png" : "image/jpeg";

async function main() {
  console.log(`Seeding ${baseUrl}`);
  const health = await api("/health");
  if (health.status !== 200) throw new Error(`/health returned ${health.status} — aborting.`);

  // credentials: reuse on re-runs so login replaces register
  let creds = {};
  if (existsSync(credsPath)) creds = JSON.parse(await readFile(credsPath, "utf8"));

  const sessions = {}; // username -> { token, userId }

  console.log("\n[1/4] Users (paced under the 10/min auth budget)...");
  for (const u of USERS) {
    let password = creds[u.username]?.password;
    let result;
    if (!password) {
      password = `Seed${Math.random().toString(36).slice(2, 10)}9x`;
      result = await api("/auth/register", {
        method: "POST",
        body: { username: u.username, email: u.email, password },
      });
      if (result.status === 200) {
        creds[u.username] = { email: u.email, password };
        await writeFile(credsPath, JSON.stringify(creds, null, 2));
      } else if (result.status === 409 || result.json?.error) {
        console.log(`  ${u.username}: exists but no saved password — skipping (delete the user or provide creds).`);
        continue;
      } else {
        throw new Error(`register ${u.username} -> ${result.status}: ${result.text}`);
      }
    } else {
      result = await api("/auth/login", {
        method: "POST",
        body: { usernameOrEmail: u.username, password },
      });
      if (result.status !== 200) {
        console.log(`  ${u.username}: login failed (${result.status}) — skipping.`);
        continue;
      }
    }
    sessions[u.username] = { token: result.json.token, userId: result.json.userId };
    console.log(`  ${u.username} ready (${result.json.userId})`);
    await sleep(1000);
  }

  const active = Object.keys(sessions);
  if (active.length === 0) throw new Error("No usable sessions — nothing to seed.");

  console.log("\n[2/4] Recipes + photos...");
  const created = []; // { id, owner, title }
  for (const r of RECIPES) {
    const session = sessions[r.owner];
    if (!session) continue;

    let imageUrl = null;
    if (imagesDir && r.image) {
      const filePath = path.join(imagesDir, r.image);
      if (existsSync(filePath)) {
        const bytes = await readFile(filePath);
        const form = new FormData();
        form.append("file", new Blob([bytes], { type: contentTypeFor(r.image) }), r.image);
        const up = await api("/images", { method: "POST", token: session.token, form });
        if (up.status === 201) {
          imageUrl = up.json.url;
        } else {
          console.log(`  image ${r.image} rejected (${up.status}): ${up.text.slice(0, 120)}`);
        }
        await sleep(3200); // images lane: 20/min
      } else {
        console.log(`  image ${r.image} not found — creating recipe without photo`);
      }
    }

    const res = await api("/recipes", {
      method: "POST",
      token: session.token,
      body: {
        title: r.title,
        description: r.description,
        prepTimeMinutes: r.prepTimeMinutes,
        cookTimeMinutes: r.cookTimeMinutes,
        servings: r.servings,
        difficulty: r.difficulty,
        cuisineType: r.cuisineType,
        caloriesPerServing: r.caloriesPerServing,
        imageUrl,
        visibility: "Public",
        ingredients: r.ingredients,
        steps: r.steps,
        tags: r.tags,
      },
    });
    if (res.status === 201 || res.status === 200) {
      created.push({ id: res.json.id, owner: r.owner, title: r.title });
      console.log(`  ${r.owner}: "${r.title}"${imageUrl ? " 📷" : ""}`);
    } else {
      console.log(`  FAILED "${r.title}" (${res.status}): ${res.text.slice(0, 200)}`);
    }
  }

  console.log("\n[3/4] Social graph (follows, likes, saves, comments)...");
  for (const [follower, followees] of Object.entries(FOLLOWS)) {
    const s = sessions[follower];
    if (!s) continue;
    for (const followee of followees) {
      const target = sessions[followee];
      if (!target) continue;
      await api(`/users/${target.userId}/follow`, { method: "POST", token: s.token });
      await sleep(150);
    }
  }
  let commentIdx = 0;
  for (const username of active) {
    const s = sessions[username];
    const others = created.filter((r) => r.owner !== username);
    // like ~60% of other people's recipes, save a couple, comment on two
    const liked = others.filter((_, i) => (i + username.length) % 5 !== 0);
    for (const r of liked) {
      await api(`/recipes/${r.id}/likes`, { method: "POST", token: s.token });
      await sleep(120);
    }
    for (const r of others.slice(0, 2)) {
      await api(`/recipes/${r.id}/saves`, { method: "POST", token: s.token });
      await sleep(120);
    }
    for (const r of [others[1], others[3]].filter(Boolean)) {
      await api(`/recipes/${r.id}/comments`, {
        method: "POST", token: s.token,
        body: { content: COMMENTS[commentIdx++ % COMMENTS.length] },
      });
      await sleep(120);
    }
    console.log(`  ${username}: ${liked.length} likes, 2 saves, 2 comments`);
  }

  console.log("\n[4/4] Verify: fetching the feed as " + active[0] + "...");
  const feed = await api("/feed?limit=50", { token: sessions[active[0]].token });
  const items = feed.json?.items ?? [];
  const withPhotos = items.filter((i) => i.recipe?.imageUrl).length;
  console.log(`  feed returned ${items.length} items (${withPhotos} with photos), source: ${feed.json?.source}`);

  console.log(`\nDone. Demo passwords are stored in ${path.basename(credsPath)} (git-ignored — do not commit).`);
}

main().catch((e) => { console.error("\nSeed failed:", e.message); process.exit(1); });
