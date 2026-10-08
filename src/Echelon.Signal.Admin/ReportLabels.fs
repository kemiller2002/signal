/// The rendered report's words in each supported locale (WI-0062, ADM-069):
/// block titles, column headings, value states, statuses and warnings.
/// Labels are presentation only: the report data, its codes and its values
/// are the same in every locale. A key a locale lacks falls back to English.
///
/// Pure.
module Echelon.Signal.Admin.ReportLabels

/// English, German, French and Arabic, in that order.
let private table: (string * (string * string * string * string)) list =
    [ "Header", ("Report", "Bericht", "Rapport", "التقرير")
      "Summary", ("Summary", "Zusammenfassung", "Synthèse", "الملخص")
      "ResponseCounts", ("Responses", "Antworten", "Réponses", "الردود")
      "OverallResult", ("Overall result", "Gesamtergebnis", "Résultat global", "النتيجة الإجمالية")
      "SectionResults", ("Results by section", "Ergebnisse nach Abschnitt", "Résultats par section", "النتائج حسب القسم")
      "GroupDistributions", ("Spread of results", "Streuung der Ergebnisse", "Dispersion des résultats", "تشتت النتائج")
      "Strengths", ("Strengths", "Stärken", "Points forts", "نقاط القوة")
      "Weaknesses", ("Areas to improve", "Verbesserungsbereiche", "Axes d'amélioration", "مجالات التحسين")
      "Recommendations", ("Recommendations", "Empfehlungen", "Recommandations", "التوصيات")
      "CoverageAndConfidence", ("Coverage", "Abdeckung", "Couverture", "التغطية")
      "Comparisons", ("Comparisons", "Vergleiche", "Comparaisons", "المقارنات")
      "RespondentDetail", ("Respondent detail", "Einzelangaben", "Détail par répondant", "تفاصيل المستجيبين")
      "RoleBreakdown", ("By role", "Nach Rolle", "Par rôle", "حسب الدور")
      "Methodology", ("Methodology", "Methodik", "Méthodologie", "المنهجية")
      "AuditMetadata", ("Audit", "Prüfangaben", "Audit", "بيانات التدقيق")
      "contents", ("Contents", "Inhalt", "Sommaire", "المحتويات")
      "generated", ("Generated", "Erstellt", "Généré le", "تاريخ الإنشاء")
      "group", ("Group", "Gruppe", "Groupe", "المجموعة")
      "section", ("Section", "Abschnitt", "Section", "القسم")
      "mean", ("Mean", "Mittelwert", "Moyenne", "المتوسط")
      "median", ("Median", "Median", "Médiane", "الوسيط")
      "range", ("Range", "Spannweite", "Étendue", "المدى")
      "scored", ("Scored", "Gewertet", "Notés", "المحتسبة")
      "unscored", ("Not scored", "Nicht gewertet", "Non notés", "غير المحتسبة")
      "expected", ("Expected", "Erwartet", "Attendues", "المتوقع")
      "accepted", ("Accepted", "Angenommen", "Acceptées", "المقبول")
      "missing", ("Missing", "Fehlend", "Manquantes", "المفقود")
      "completion", ("Completion", "Vollständigkeit", "Taux de complétion", "نسبة الاكتمال")
      "answered", ("Answered", "Beantwortet", "Répondu", "تمت الإجابة")
      "special", ("Don't know, not observed or not applicable", "Weiß nicht, nicht beobachtet oder nicht zutreffend", "Ne sait pas, non observé ou sans objet", "لا أعرف أو لم يُلاحظ أو لا ينطبق")
      "unanswered", ("Unanswered", "Unbeantwortet", "Sans réponse", "بدون إجابة")
      "complete", ("Complete", "Vollständig", "Complet", "مكتمل")
      "partial", ("Partial", "Teilweise", "Partiel", "جزئي")
      "insufficient", ("Insufficient data", "Unzureichende Daten", "Données insuffisantes", "بيانات غير كافية")
      "notAvailable", ("Not available", "Nicht verfügbar", "Non disponible", "غير متاح")
      "notApplicable", ("Not applicable", "Nicht zutreffend", "Sans objet", "لا ينطبق")
      "insufficientResponses", ("Too few responses", "Zu wenige Antworten", "Trop peu de réponses", "عدد الردود غير كافٍ")
      "suppressed", ("Withheld to protect anonymity", "Zum Schutz der Anonymität zurückgehalten", "Masqué pour protéger l'anonymat", "محجوب لحماية الهوية")
      "notComparable", ("Not comparable", "Nicht vergleichbar", "Non comparable", "غير قابل للمقارنة")
      "low-coverage", ("Many answers are not numeric; coverage is low.", "Viele Antworten sind nicht numerisch; die Abdeckung ist gering.", "Beaucoup de réponses ne sont pas numériques ; la couverture est faible.", "كثير من الإجابات غير رقمية؛ التغطية منخفضة.")
      "low-response-count", ("Few responses were received.", "Es gingen wenige Antworten ein.", "Peu de réponses ont été reçues.", "وصل عدد قليل من الردود.")
      "high-dont-know-rate", ("Many respondents answered \"don't know\".", "Viele antworteten mit „weiß nicht“.", "Beaucoup ont répondu « ne sait pas ».", "أجاب كثيرون بـ«لا أعرف».")
      "incomplete-group", ("Not every expected response has arrived.", "Nicht alle erwarteten Antworten liegen vor.", "Toutes les réponses attendues ne sont pas arrivées.", "لم تصل جميع الردود المتوقعة.")
      "not-comparable-to-prior-version", ("Not comparable to the prior version.", "Nicht mit der Vorversion vergleichbar.", "Non comparable à la version précédente.", "غير قابل للمقارنة بالإصدار السابق.")
      "anonymous-small-group-suppressed", ("Results are withheld: the anonymous group is below its minimum size.", "Ergebnisse zurückgehalten: Die anonyme Gruppe liegt unter ihrer Mindestgröße.", "Résultats masqués : le groupe anonyme est sous sa taille minimale.", "النتائج محجوبة: المجموعة المجهولة أقل من حدها الأدنى.") ]

let private byKey = Map.ofList table

/// A label in a locale, falling back to English, then to the key itself.
let label (tag: string) (key: string) =
    match byKey.TryFind key with
    | Some(en, de, fr, ar) ->
        match tag with
        | "de-DE" -> de
        | "fr-FR" -> fr
        | "ar-EG" -> ar
        | _ -> en
    | None -> key

/// Every key, for the completeness test.
let keys = table |> List.map fst
