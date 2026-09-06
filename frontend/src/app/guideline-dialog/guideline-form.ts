/** The editable shape of a guideline file, held by the shell and passed into the deferred dialog. */
export interface GuidelineForm {
  id: string;
  enabled: boolean;
  priority: number;
  kinds: string;
  levels: string;
  content: string;
}
