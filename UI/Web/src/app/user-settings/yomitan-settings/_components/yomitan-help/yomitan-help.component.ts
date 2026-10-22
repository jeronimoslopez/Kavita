import {ChangeDetectionStrategy, Component, input, signal} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {SafeHtmlPipe} from "../../../../_pipes/safe-html.pipe";

/**
 * Yomitan's "More…" / "Less…" toggle for a setting's longer explanation.
 */
@Component({
  selector: 'app-yomitan-help',
  imports: [
    TranslocoDirective,
    SafeHtmlPipe
  ],
  templateUrl: './yomitan-help.component.html',
  styleUrl: './yomitan-help.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanHelpComponent {
  /** Already translated, may contain markup */
  html = input.required<string>();
  /** Starts open, for explanations that should always be visible */
  expanded = input(false);

  protected readonly open = signal<boolean | null>(null);

  toggle() {
    this.open.set(!(this.open() ?? this.expanded()));
  }
}
