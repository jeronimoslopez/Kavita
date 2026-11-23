import {ChangeDetectionStrategy, Component, signal} from '@angular/core';
import {TestBed} from '@angular/core/testing';

@Component({
  selector: 'app-runner-probe',
  template: `<span>{{count()}}</span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class RunnerProbeComponent {
  readonly count = signal(0);
}

describe('unit test runner', () => {
  it('renders a standalone component and updates it from a signal without zone.js', async () => {
    const fixture = TestBed.createComponent(RunnerProbeComponent);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toBe('0');

    fixture.componentInstance.count.set(2);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toBe('2');
  });
});
