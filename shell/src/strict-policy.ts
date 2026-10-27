import { z } from "zod";

/**
 * What the shell sets before any other module loads, for the content security policy (ADR-0053). Zod compiles its
 * object parsers with new Function unless told not to, and decides when each schema is made, as the modules load: it
 * first tries whether it may, and the policy allows no eval, so that try is a violation on every page. Interpreted
 * parsing is all the shell needs. Imported first by every entry point.
 */
z.config({ jitless: true });
