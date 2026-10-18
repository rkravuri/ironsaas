import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import en from "./en.json";

/** English only in S1 (TF-170 brings locales); every string already goes through the catalogue. */
void i18n.use(initReactI18next).init({
  resources: { en: { translation: en } },
  lng: "en",
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

export default i18n;
