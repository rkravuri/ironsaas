import type { ComponentType } from "react";
import { AllDone } from "./AllDone";
import { Broken } from "./Broken";
import type { IllustrationProps } from "./Drawing";
import { Forbidden } from "./Forbidden";
import { Home } from "./Home";
import { Loading } from "./Loading";
import { Lost } from "./Lost";
import type { IllustrationName } from "./names";
import { NoAccess } from "./NoAccess";
import { NothingFound } from "./NothingFound";
import { NothingYet } from "./NothingYet";
import { Success } from "./Success";
import { Welcome } from "./Welcome";

/** Every drawing by its name, for the gallery and the test that mounts each one. */
export const illustrations: Record<IllustrationName, ComponentType<IllustrationProps>> = {
  nothingYet: NothingYet,
  nothingFound: NothingFound,
  noAccess: NoAccess,
  allDone: AllDone,
  lost: Lost,
  broken: Broken,
  forbidden: Forbidden,
  welcome: Welcome,
  home: Home,
  success: Success,
  loading: Loading,
};
