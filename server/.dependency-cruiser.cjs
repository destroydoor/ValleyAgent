/** @type {import('dependency-cruiser').IConfiguration} */
module.exports = {
  forbidden: [
    {
      name: "core-cannot-depend-on-stardew",
      severity: "error",
      from: { path: "^packages/core/src/" },
      to: { path: "^packages/stardew/" }
    },
    {
      name: "core-cannot-depend-on-gamespecific",
      severity: "error",
      from: { path: "^packages/core/src/" },
      to: { path: "^packages/(?!core)" }
    }
  ]
};
